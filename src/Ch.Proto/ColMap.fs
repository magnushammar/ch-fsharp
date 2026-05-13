namespace Ch.Proto

open System
open System.Collections.Generic

/// `Map(K, V)` — variable-length set of key→value pairs per row. Wire format
/// per block (same shape as Array but two parallel inner columns):
///   Offsets : UInt64 LE × N    ; cumulative pair count, offset[i] = end of row i
///   Keys    : flat key column body, last_offset entries total
///   Values  : flat value column body, last_offset entries total
///
/// State header (LowCardinality, …) forwards to keys first, then values —
/// matches ch-go's `proto/col_map.go` field order.
[<Sealed>]
type ColMap<'K, 'V when 'K : equality and 'K : not null>(keys: IColumnOf<'K>, values: IColumnOf<'V>) =
    let offsets = ColUInt64()

    member _.Type = "Map(" + keys.Type + ", " + values.Type + ")"
    member _.Rows = offsets.Rows

    /// Cumulative offsets (UInt64). `Offsets.Row(i) = end-of-row-i` in Keys/Values.
    member _.Offsets = offsets
    /// Flat keys column.
    member _.Keys = keys
    /// Flat values column.
    member _.Values = values

    member _.Reset() =
        offsets.Reset()
        keys.Reset()
        values.Reset()

    /// Number of key→value pairs in row i.
    member _.RowLen(i: int) : int =
        let endIdx = int (offsets.Row(i))
        let startIdx = if i = 0 then 0 else int (offsets.Row(i - 1))
        endIdx - startIdx

    /// Materialise row i as a fresh `Dictionary<'K, 'V>`.
    member _.Row(i: int) : Dictionary<'K, 'V> =
        let endIdx = int (offsets.Row(i))
        let startIdx = if i = 0 then 0 else int (offsets.Row(i - 1))
        let length = endIdx - startIdx
        let result = Dictionary<'K, 'V>(length)
        for idx in startIdx .. endIdx - 1 do
            result.[keys.Row(idx)] <- values.Row(idx)
        result

    /// Materialise row i as an ordered `KeyValuePair[]` — preserves wire order,
    /// no Dictionary allocation. Useful when you need stable iteration or when
    /// the inner keys aren't hashable.
    member _.RowKV(i: int) : KeyValuePair<'K, 'V> array =
        let endIdx = int (offsets.Row(i))
        let startIdx = if i = 0 then 0 else int (offsets.Row(i - 1))
        let length = endIdx - startIdx
        let result : KeyValuePair<'K, 'V> array = Array.zeroCreate length
        for j in 0 .. length - 1 do
            result.[j] <- KeyValuePair(keys.Row(startIdx + j), values.Row(startIdx + j))
        result

    /// Append a row from any dictionary. Iteration order is .NET's
    /// `IEnumerable<KeyValuePair>` order on the input — not guaranteed across
    /// dict types. Use `AppendKV` if you need deterministic byte output.
    member _.Append(m: IReadOnlyDictionary<'K, 'V>) =
        for kv in m do
            keys.Append(kv.Key)
            values.Append(kv.Value)
        offsets.Append(uint64 keys.Rows)

    /// Append a row from an ordered list of KV pairs — preserves insertion
    /// order on the wire.
    member _.AppendKV(kv: KeyValuePair<'K, 'V> array) =
        for pair in kv do
            keys.Append(pair.Key)
            values.Append(pair.Value)
        offsets.Append(uint64 keys.Rows)

    member _.DecodeColumn(r: Reader, n: int) =
        if n = 0 then () else
        offsets.DecodeColumn(r, n)
        let totalInner = int (offsets.Row(n - 1))
        keys.DecodeColumn(r, totalInner)
        values.DecodeColumn(r, totalInner)

    member _.EncodeColumn(b: Buf) =
        offsets.EncodeColumn(b)
        keys.EncodeColumn(b)
        values.EncodeColumn(b)

    member _.DecodeState(r: Reader) =
        match keys with
        | :? IStatefulColumn as s -> s.DecodeState(r)
        | _ -> ()
        match values with
        | :? IStatefulColumn as s -> s.DecodeState(r)
        | _ -> ()

    member _.EncodeState(b: Buf) =
        match keys with
        | :? IStatefulColumn as s -> s.EncodeState(b)
        | _ -> ()
        match values with
        | :? IStatefulColumn as s -> s.EncodeState(b)
        | _ -> ()

    interface IColumnResult with
        member this.Type = this.Type
        member this.Rows = this.Rows
        member this.Reset() = this.Reset()
        member this.DecodeColumn(r, n) = this.DecodeColumn(r, n)
        member this.EncodeColumn(b) = this.EncodeColumn(b)

    interface IColumnOf<Dictionary<'K, 'V>> with
        member this.Append(v) = this.Append(v :> IReadOnlyDictionary<'K, 'V>)
        member this.Row(i) = this.Row(i)

    interface IStatefulColumn with
        member this.DecodeState(r) = this.DecodeState(r)
        member this.EncodeState(b) = this.EncodeState(b)
