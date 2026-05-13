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
[<Sealed>]
type ColLowCardinality<'T when 'T : equality and 'T : not null>(inner: IColumnOf<'T>) =

    // Wire-side bit flags from ch-go's compress/clickhouse-cpp lineage.
    static let updateAllFlags = (1L <<< 9) ||| (1L <<< 10)  // hasAdditionalKeys | needUpdateDict
    static let keyMask = 0xFFL

    // Read-side state.
    let mutable keys : byte array = Array.Empty<byte>()
    let mutable keyWidth : int = 1
    let mutable rowCount : int = 0
    let mutable dictArray : 'T array = Array.Empty<'T>()
    let mutable dictRows : int = 0

    // Write-side state — allocated eagerly. Cheap, and the read-only case
    // pays one unused ResizeArray + Dictionary per column instance.
    let values = ResizeArray<'T>()
    let dedup = Dictionary<'T, int>()

    let ensureKeys (n: int) =
        if keys.Length < n then
            Array.Resize(&keys, max n (max 64 (keys.Length * 2)))

    let ensureDict (n: int) =
        if dictArray.Length < n then
            dictArray <- Array.zeroCreate (max n (max 16 (dictArray.Length * 2)))

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

    /// Walk Values, build inner column + key indices, pick key width.
    /// Called automatically before EncodeColumn — exposed for tests.
    member _.Prepare() =
        if values.Count = 0 then
            rowCount <- 0
        else
            dedup.Clear()
            inner.Reset()
            for v in values do
                if not (dedup.ContainsKey v) then
                    dedup.[v] <- inner.Rows
                    inner.Append(v)
            // Now pick key width based on dict size.
            let kw, _ = widthForCardinality inner.Rows
            keyWidth <- kw
            rowCount <- values.Count
            ensureKeys (rowCount * keyWidth)
            let mutable off = 0
            for v in values do
                writeKeyAt off (dedup.[v])
                off <- off + keyWidth
            dictRows <- inner.Rows

    // ── IColumnResult / IColumnOf<'T> interface surface ────────────

    member _.Type = "LowCardinality(" + inner.Type + ")"

    /// Eager row count, matching ch-go's `ColLowCardinality[T].Rows() =
    /// len(c.Values)`. **Must reflect staged input rows BEFORE `Prepare`
    /// runs**, otherwise `Client.fs: EncodeDataBlock` writes a block
    /// header with rows=0 and the server silently accepts the empty
    /// block (no exception, complete data loss). Returning
    /// `max values.Count rowCount` covers both sides:
    ///   - write side: `values.Count` is bumped on every Append.
    ///   - read side: `rowCount` is set in DecodeColumn; values is unused
    ///     because we materialise the dictionary, not a dense `Values`
    ///     list (DESIGN_CHOICES §8).
    member _.Rows = max values.Count rowCount

    /// Exposed for perf-sensitive consumers: iterate Keys + Inner directly to
    /// avoid Row(i)'s per-row branch on keyWidth.
    member _.Inner = inner
    member _.Keys : ReadOnlySpan<byte> = ReadOnlySpan(keys, 0, rowCount * keyWidth)
    member _.KeyWidth = keyWidth
    member _.DictRows = dictRows
    /// Materialised dictionary entries: dictArray[k] = inner.Row(k) for
    /// k in 0..DictRows-1. Lifetime is until next DecodeColumn.
    member _.Dictionary : ReadOnlySpan<'T> = ReadOnlySpan(dictArray, 0, dictRows)

    member _.Reset() =
        rowCount <- 0
        dictRows <- 0
        values.Clear()
        inner.Reset()

    /// Append a value (write-side). Dedup happens in Prepare() before encode.
    member _.Append(v: 'T) = values.Add(v)

    /// Row i — fast path: dictArray indexed by readKeyAt(i).
    member _.Row(i: int) : 'T =
        dictArray.[readKeyAt i]

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
            // Materialise the dict ONCE per block — see DESIGN_CHOICES §8.
            for k in 0 .. dRows - 1 do
                dictArray.[k] <- inner.Row(k)

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
