namespace Ch.Proto

open System
open System.Runtime.InteropServices

/// `Array(T)` — variable-length list of T per row. Wire format per block:
///   Offsets : UInt64 LE × N    ; cumulative count, offset[i] = end of row i
///   Inner   : the flat values column body, last_offset elements total
///
/// Offsets storage: raw `uint64[]` + count, written/read directly via
/// `MemoryMarshal.AsBytes`. LE x64 — wire bytes are bit-identical to a
/// `ColUInt64`-backed implementation.
///
/// Recursive: `Array(Array(T))` works because `ColArr<'T>` itself implements
/// `IColumnOf<'T[]>`. ch-go reference: `proto/col_arr.go`.
[<Sealed>]
type ColArr<'T>(inner: IColumnOf<'T>) =
    let mutable offsets : uint64 array = Array.Empty<uint64>()
    let mutable offsetsCount : int = 0

    let ensureOffsets (n: int) =
        if offsets.Length < n then
            Array.Resize(&offsets, max n (max 8 (offsets.Length * 2)))

    member _.Type = "Array(" + inner.Type + ")"
    member _.Rows = offsetsCount

    /// Number of offset entries currently held (one per array row).
    member _.OffsetsCount = offsetsCount
    /// Zero-copy view of the cumulative offsets (UInt64 each).
    /// `Span.[i] = end-of-row-i` in `Inner`. Lifetime: valid only until the
    /// next `Append` / `AppendSpan` / `DecodeColumn` / `Reset`.
    member _.OffsetsSpan : ReadOnlySpan<uint64> =
        ReadOnlySpan(offsets, 0, offsetsCount)
    /// Flat values column.
    member _.Inner = inner

    member _.Reset() =
        offsetsCount <- 0
        inner.Reset()

    /// Length of row i.
    member _.RowLen(i: int) : int =
        let endIdx = int offsets.[i]
        let startIdx = if i = 0 then 0 else int offsets.[i - 1]
        endIdx - startIdx

    /// Materialise row i as a fresh `'T[]`.
    member _.Row(i: int) : 'T array =
        let endIdx = int offsets.[i]
        let startIdx = if i = 0 then 0 else int offsets.[i - 1]
        let length = endIdx - startIdx
        let result : 'T array = Array.zeroCreate length
        for j in 0 .. length - 1 do
            result.[j] <- inner.Row(startIdx + j)
        result

    /// Zero-copy view of row i when `inner` is a bulk-readable primitive
    /// (`ColPrimitive<'T>`) — returns a slice into the inner column's typed
    /// buffer. For non-primitive inner (`ColStr`, `ColEnum*`, etc.) it
    /// falls back to allocating `Row(i)` and returning a span over the
    /// result — still correct, just no longer zero-alloc.
    /// Lifetime: aliases inner's buffer, valid only until the next
    /// `DecodeColumn` / `Reset` / `Append` on the inner column.
    member this.RowSpan(i: int) : ReadOnlySpan<'T> =
        let endIdx = int offsets.[i]
        let startIdx = if i = 0 then 0 else int offsets.[i - 1]
        let length = endIdx - startIdx
        match box inner with
        | :? IBulkReadable<'T> as bulk ->
            bulk.AsSpan().Slice(startIdx, length)
        | _ ->
            ReadOnlySpan<'T>(this.Row(i))

    /// Append a row of values from an array. Delegates to `AppendSpan` so
    /// the bulk-copy dispatch is shared.
    member this.Append(values: 'T array) =
        this.AppendSpan(ReadOnlySpan(values))

    /// Append a row of values from a span. When `inner` implements
    /// `IBulkAppendable<'T>` (i.e. is a `ColPrimitive<'T>`), the values
    /// are copied in one shot via `MemoryMarshal.Cast` — no per-element
    /// `Append`. For non-primitive inner (`ColStr`, `ColEnum*`, etc.)
    /// the per-row fallback runs.
    member _.AppendSpan(values: ReadOnlySpan<'T>) =
        match box inner with
        | :? IBulkAppendable<'T> as bulk -> bulk.AppendRange(values)
        | _ ->
            for i in 0 .. values.Length - 1 do
                inner.Append(values.[i])
        ensureOffsets (offsetsCount + 1)
        offsets.[offsetsCount] <- uint64 inner.Rows
        offsetsCount <- offsetsCount + 1

    member _.DecodeColumn(r: Reader, n: int) =
        ensureOffsets n
        if n > 0 then
            r.ReadFull(MemoryMarshal.AsBytes(offsets.AsSpan(0, n)))
        offsetsCount <- n
        let totalInner = if n > 0 then int offsets.[n - 1] else 0
        inner.DecodeColumn(r, totalInner)

    member _.EncodeColumn(b: Buf) =
        if offsetsCount > 0 then
            b.PutRaw(MemoryMarshal.AsBytes(
                ReadOnlySpan(offsets, 0, offsetsCount)))
        inner.EncodeColumn(b)

    /// State forwards to inner — Array(LowCardinality(...)) needs this.
    member _.DecodeState(r: Reader) =
        match inner with
        | :? IStatefulColumn as s -> s.DecodeState(r)
        | _ -> ()

    member _.EncodeState(b: Buf) =
        match inner with
        | :? IStatefulColumn as s -> s.EncodeState(b)
        | _ -> ()

    interface IColumnResult with
        member this.Type = this.Type
        member this.Rows = this.Rows
        member this.Reset() = this.Reset()
        member this.DecodeColumn(r, n) = this.DecodeColumn(r, n)
        member this.EncodeColumn(b) = this.EncodeColumn(b)

    interface IColumnOf<'T array> with
        member this.Append(v) = this.Append(v)
        member this.Row(i) = this.Row(i)

    interface IStatefulColumn with
        member this.DecodeState(r) = this.DecodeState(r)
        member this.EncodeState(b) = this.EncodeState(b)

    /// Recursive composition: `Array(Array(T))` → wrap self in another ColArr.
    interface IArrayable with
        member this.Array() =
            ColArr<'T array>(this :> IColumnOf<'T array>) :> IColumnResult
