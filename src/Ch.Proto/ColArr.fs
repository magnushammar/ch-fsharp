namespace Ch.Proto

open System

/// `Array(T)` — variable-length list of T per row. Wire format per block:
///   Offsets : UInt64 LE × N    ; cumulative count, offset[i] = end of row i
///   Inner   : the flat values column body, last_offset elements total
///
/// Recursive: `Array(Array(T))` works because `ColArr<'T>` itself implements
/// `IColumnOf<'T[]>`. ch-go reference: `proto/col_arr.go`.
[<Sealed>]
type ColArr<'T>(inner: IColumnOf<'T>) =
    let offsets = ColUInt64()

    member _.Type = "Array(" + inner.Type + ")"
    member _.Rows = offsets.Rows

    /// Cumulative offsets (UInt64). `Offsets.Row(i) = end-of-row-i` in Inner.
    member _.Offsets = offsets
    /// Flat values column.
    member _.Inner = inner

    member _.Reset() =
        offsets.Reset()
        inner.Reset()

    /// Length of row i.
    member _.RowLen(i: int) : int =
        let endIdx = int (offsets.Row(i))
        let startIdx = if i = 0 then 0 else int (offsets.Row(i - 1))
        endIdx - startIdx

    /// Materialise row i as a fresh `'T[]`.
    member _.Row(i: int) : 'T array =
        let endIdx = int (offsets.Row(i))
        let startIdx = if i = 0 then 0 else int (offsets.Row(i - 1))
        let length = endIdx - startIdx
        let result : 'T array = Array.zeroCreate length
        for j in 0 .. length - 1 do
            result.[j] <- inner.Row(startIdx + j)
        result

    /// Append a row of values from an array.
    member _.Append(values: 'T array) =
        if values.Length > 0 then
            for v in values do inner.Append(v)
        offsets.Append(uint64 inner.Rows)

    /// Append a row of values from a span (zero-allocation entry).
    member _.AppendSpan(values: ReadOnlySpan<'T>) =
        for i in 0 .. values.Length - 1 do
            inner.Append(values.[i])
        offsets.Append(uint64 inner.Rows)

    member _.DecodeColumn(r: Reader, n: int) =
        offsets.DecodeColumn(r, n)
        let totalInner =
            if n > 0 then int (offsets.Row(n - 1)) else 0
        inner.DecodeColumn(r, totalInner)

    member _.EncodeColumn(b: Buf) =
        offsets.EncodeColumn(b)
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
