namespace Ch.Proto

open System
open System.Buffers.Binary
open System.Collections.Generic
open System.IO

/// `LowCardinality(T)` — wraps a dictionary inner column with a per-row keys
/// array. ch-go reference: `proto/col_low_cardinality.go`.
///
/// Wire format per block (column body, called by Block.decode):
///   meta       int64 LE        ; (updateAll flags) | keyTypeNibble
///   dictRows   int64 LE
///   <dict bytes — inner.EncodeColumn / DecodeColumn>
///   keyRows    int64 LE        ; == rowCount
///   keys       rowCount * keyWidth bytes
///
/// State header (separate, invoked via IStatefulColumn before the body):
///   keySerVersion int64 LE = 1
///   <inner state if inner implements IStatefulColumn>
///
/// We DEPART from ch-go on read: we materialise the dictionary as a
/// `'T[] dictArray` of size dictRows (not a dense `Values []T` of size
/// rowCount). See plans/DESIGN_CHOICES.md §8.
///
/// Append-time dedup: the dictionary is built inline as values arrive
/// (`Append` → `dedup.TryGetValue` → either reuse key or push to inner
/// + record new key). `Prepare` then just packs the key indices into
/// the configured byte width. Saves the staged-values buffer
/// (`ResizeArray<'T>`) and one extra pass at encode time.
///
/// Optional `keyComparer` overrides the default `EqualityComparer<'T>`
/// — required for `byte[]` keys (content-hash via
/// `ByteArrayContentEqualityComparer`) because default array equality
/// is reference-based.
[<Sealed>]
type ColLowCardinality<'T when 'T : equality and 'T : not null>
    (inner: IColumnOf<'T>, ?keyComparer: IEqualityComparer<'T>) =

    // Wire-side bit flags from ch-go's compress/clickhouse-cpp lineage.
    static let updateAllFlags = (1L <<< 9) ||| (1L <<< 10)  // hasAdditionalKeys | needUpdateDict
    static let keyMask = 0xFFL

    // Read-side state.
    let mutable keys : byte array = Array.Empty<byte>()
    let mutable keyWidth : int = 1
    let mutable rowCount : int = 0
    let mutable dictArray : 'T array = Array.Empty<'T>()
    let mutable dictRows : int = 0
    /// Lazy flag — dictArray is populated via `inner.Row(k)` only when
    /// `Row(i)` / `Dictionary` is first called after a `DecodeColumn`.
    /// Byte-span consumers (`RowSpan`) bypass this entirely and skip the
    /// per-block string/value allocations.
    let mutable dictMaterialized : bool = false

    // Write-side state — allocated eagerly. dedup grows as Append sees
    // new values; tempKeys collects one int per Append. Both reset at
    // block boundaries via Reset().
    //
    // Comparer selection:
    //   * caller's `keyComparer` wins if supplied;
    //   * otherwise, for `'T = byte[]` we auto-supply
    //     `ByteArrayContentEqualityComparer.Instance` because default
    //     array equality is reference-based — using it would silently
    //     break dedup on `LowCardinality(FixedString(N))`. This is the
    //     "we know exactly one safe default for this 'T" case;
    //     callers who really want reference equality on byte[] can
    //     pass `HashIdentity.Reference` (or similar) explicitly.
    //   * otherwise, `Dictionary<'T, int>()` picks
    //     `EqualityComparer<'T>.Default`, which is content-correct for
    //     primitives and `string`.
    let dedup : Dictionary<'T, int> =
        match keyComparer with
        | Some c -> Dictionary<'T, int>(c)
        | None when typeof<'T> = typeof<byte array> ->
            match box ByteArrayContentEqualityComparer.Instance with
            | :? IEqualityComparer<'T> as ic -> Dictionary<'T, int>(ic)
            | _ -> Dictionary<'T, int>()  // unreachable: typeof check passed
        | None -> Dictionary<'T, int>()
    let tempKeys = ResizeArray<int>()

    let ensureKeys (n: int) =
        if keys.Length < n then
            Array.Resize(&keys, max n (max 64 (keys.Length * 2)))

    let ensureDict (n: int) =
        if dictArray.Length < n then
            dictArray <- Array.zeroCreate (max n (max 16 (dictArray.Length * 2)))

    let ensureDictMaterialized () =
        if not dictMaterialized then
            for k in 0 .. dictRows - 1 do
                dictArray.[k] <- inner.Row(k)
            dictMaterialized <- true

    let widthForCardinality (n: int) : int * int =
        // (keyWidth, keyTypeNibble)
        if n <= int Byte.MaxValue + 1 then 1, 0
        elif n <= int UInt16.MaxValue + 1 then 2, 1
        elif uint32 n <= UInt32.MaxValue then 4, 2
        else 8, 3

    let readKeyAt (i: int) : int =
        let off = i * keyWidth
        match keyWidth with
        | 1 -> int keys.[off]
        | 2 -> int (BinaryPrimitives.ReadUInt16LittleEndian(ReadOnlySpan(keys, off, 2)))
        | 4 -> int (BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan(keys, off, 4)))
        | 8 -> int (BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan(keys, off, 8)))
        | other -> raise (InvalidOperationException $"bad keyWidth {other}")

    let writeKeyAt (off: int) (idx: int) =
        match keyWidth with
        | 1 -> keys.[off] <- byte idx
        | 2 -> BinaryPrimitives.WriteUInt16LittleEndian(keys.AsSpan(off, 2), uint16 idx)
        | 4 -> BinaryPrimitives.WriteUInt32LittleEndian(keys.AsSpan(off, 4), uint32 idx)
        | 8 -> BinaryPrimitives.WriteUInt64LittleEndian(keys.AsSpan(off, 8), uint64 idx)
        | other -> raise (InvalidOperationException $"bad keyWidth {other}")

    /// Pick key width based on inline-built dict size and pack the
    /// staged key indices into the keys byte buffer. The dedup +
    /// inner.Append happen during `Append` (eagerly), so here we
    /// only do the keyWidth-dependent byte packing.
    /// Called automatically before EncodeColumn — exposed for tests.
    member _.Prepare() =
        if tempKeys.Count = 0 then
            rowCount <- 0
        else
            let kw, _ = widthForCardinality inner.Rows
            keyWidth <- kw
            rowCount <- tempKeys.Count
            ensureKeys (rowCount * keyWidth)
            let mutable off = 0
            for k in tempKeys do
                writeKeyAt off k
                off <- off + keyWidth
            dictRows <- inner.Rows

    // ── IColumnResult / IColumnOf<'T> interface surface ────────────

    member _.Type = "LowCardinality(" + inner.Type + ")"

    /// Eager row count, matching ch-go's `ColLowCardinality[T].Rows() =
    /// len(c.Values)`. **Must reflect staged input rows BEFORE `Prepare`
    /// runs**, otherwise `Client.fs: EncodeDataBlock` writes a block
    /// header with rows=0 and the server silently accepts the empty
    /// block (no exception, complete data loss). Returning
    /// `max tempKeys.Count rowCount` covers both sides:
    ///   - write side: `tempKeys.Count` is bumped on every `Append`.
    ///   - read side: `rowCount` is set in `DecodeColumn`; `tempKeys`
    ///     is unused because we materialise the dictionary, not a
    ///     dense `Values` list (DESIGN_CHOICES §8).
    member _.Rows = max tempKeys.Count rowCount

    /// Exposed for perf-sensitive consumers: iterate Keys + Inner directly to
    /// avoid Row(i)'s per-row branch on keyWidth. Method (not property)
    /// for surface consistency with the other span accessors.
    member _.Inner = inner
    member _.Keys() : ReadOnlySpan<byte> = ReadOnlySpan(keys, 0, rowCount * keyWidth)
    member _.KeyWidth = keyWidth
    member _.DictRows = dictRows
    /// Materialised dictionary entries: `Dictionary().[k] = inner.Row(k)`
    /// for k in 0..DictRows-1. Materialisation is lazy — the first
    /// `Dictionary` / `Row(i)` call after `DecodeColumn` populates the
    /// array; byte-span consumers using `RowSpan(i)` skip materialisation
    /// entirely. Method (not property) for surface consistency.
    /// Lifetime: valid until next `DecodeColumn` / `Reset`.
    member _.Dictionary() : ReadOnlySpan<'T> =
        ensureDictMaterialized ()
        ReadOnlySpan(dictArray, 0, dictRows)

    member _.Reset() =
        rowCount <- 0
        dictRows <- 0
        dictMaterialized <- false
        dedup.Clear()
        tempKeys.Clear()
        inner.Reset()

    /// Append a value (write-side). Dedup happens inline: existing values
    /// hit the dedup dictionary and reuse the key; new values are pushed
    /// to `inner` and registered. `Prepare` then only has to pick the
    /// key byte width and pack `tempKeys`.
    member _.Append(v: 'T) =
        let mutable idx = 0
        if dedup.TryGetValue(v, &idx) then
            tempKeys.Add(idx)
        else
            idx <- inner.Rows
            dedup.[v] <- idx
            inner.Append(v)
            tempKeys.Add(idx)

    /// Row i — fast path: dictArray indexed by readKeyAt(i). The dict
    /// is materialised lazily on the first `Row` / `Dictionary` call
    /// after each `DecodeColumn`; subsequent calls hit the cached array
    /// (the materialised branch becomes the predicted path).
    member _.Row(i: int) : 'T =
        ensureDictMaterialized ()
        dictArray.[readKeyAt i]

    /// Zero-allocation byte view of row i — supported when `inner` is a
    /// byte-row column (`ColStr`, `ColFixedStr`). Skips `dictArray`
    /// materialisation entirely. Caller decodes UTF-8 / converts only
    /// if they need a managed `'T`. Lifetime: aliases inner's bytes,
    /// valid only until next `DecodeColumn` / `Reset` on the inner.
    /// Raises `NotSupportedException` if `inner` doesn't implement
    /// `IRowBytes`.
    member _.RowSpan(i: int) : ReadOnlySpan<byte> =
        let bytes : IRowBytes =
            match box inner with
            | :? IRowBytes as b -> b
            | _ ->
                raise (NotSupportedException
                    $"ColLowCardinality.RowSpan requires byte-row inner (ColStr / ColFixedStr); got {inner.GetType().Name}")
        bytes.RowBytes(readKeyAt i)

    member this.DecodeColumn(r: Reader, n: int) =
        if n = 0 then
            // ch-go also skips entirely; the column body is empty for blank
            // LC columns (`if c.Rows() == 0 { return }` in encode).
            rowCount <- 0
            dictRows <- 0
        else
            let meta = r.Int64()
            if (meta &&& (1L <<< 8)) <> 0L then
                raise (InvalidDataException "LowCardinality: global dictionary not supported")
            if (meta &&& (1L <<< 9)) = 0L then
                raise (InvalidDataException "LowCardinality: additional-keys bit missing")
            let keyNibble = int (meta &&& keyMask)
            keyWidth <-
                match keyNibble with
                | 0 -> 1
                | 1 -> 2
                | 2 -> 4
                | 3 -> 8
                | _ -> raise (InvalidDataException $"LowCardinality: bad keyType {keyNibble}")

            let dRows = int (r.Int64())
            if dRows < 0 then raise (InvalidDataException $"bad dictRows {dRows}")
            inner.DecodeColumn(r, dRows)
            dictRows <- dRows
            ensureDict dRows
            // Dict is materialised lazily on first `Row(i)` / `Dictionary` —
            // byte-span consumers using `RowSpan(i)` skip it entirely.
            dictMaterialized <- false

            let kRows = int (r.Int64())
            if kRows <> n then
                raise (InvalidDataException $"LowCardinality keyRows {kRows} != block rows {n}")
            let keyBytes = n * keyWidth
            ensureKeys keyBytes
            if keyBytes > 0 then
                r.ReadFull(keys.AsSpan(0, keyBytes))
            rowCount <- n

    member this.EncodeColumn(b: Buf) =
        this.Prepare()
        if rowCount = 0 then () else
        // meta
        let _, keyNibble = widthForCardinality dictRows
        let meta = updateAllFlags ||| int64 keyNibble
        b.PutInt64(meta)
        b.PutInt64(int64 dictRows)
        inner.EncodeColumn(b)
        b.PutInt64(int64 rowCount)
        b.PutRaw(ReadOnlySpan(keys, 0, rowCount * keyWidth))

    member _.DecodeState(r: Reader) =
        let ver = r.Int64()
        if ver <> 1L then
            raise (InvalidDataException $"LowCardinality keySerializationVersion {ver}, expected 1")
        // Inner state (most columns have none — ColStr / ColPrimitive don't).
        match inner with
        | :? IStatefulColumn as s -> s.DecodeState(r)
        | _ -> ()

    member _.EncodeState(b: Buf) =
        b.PutInt64(1L)
        match inner with
        | :? IStatefulColumn as s -> s.EncodeState(b)
        | _ -> ()

    interface IColumnResult with
        member this.Type = this.Type
        member this.Rows = this.Rows
        member this.Reset() = this.Reset()
        member this.DecodeColumn(r, n) = this.DecodeColumn(r, n)
        member this.EncodeColumn(b) = this.EncodeColumn(b)

    interface IColumnOf<'T> with
        member this.Append(v) = this.Append(v)
        member this.Row(i) = this.Row(i)

    interface IStatefulColumn with
        member this.DecodeState(r) = this.DecodeState(r)
        member this.EncodeState(b) = this.EncodeState(b)

    /// Recursive composition: `Array(LowCardinality(T))` → wrap self in a ColArr.
    interface IArrayable with
        member this.Array() =
            ColArr<'T>(this :> IColumnOf<'T>) :> IColumnResult
