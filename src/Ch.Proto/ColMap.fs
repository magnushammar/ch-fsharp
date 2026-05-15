namespace Ch.Proto

open System
open System.Collections.Generic
open System.Runtime.InteropServices

/// `Map(K, V)` — variable-length set of key→value pairs per row. Wire format
/// per block (same shape as Array but two parallel inner columns):
///   Offsets : UInt64 LE × N    ; cumulative pair count, offset[i] = end of row i
///   Keys    : flat key column body, last_offset entries total
///   Values  : flat value column body, last_offset entries total
///
/// Offsets storage: raw `uint64[]` + count, accessed via `MemoryMarshal.AsBytes`
/// for the wire I/O. LE x64 — wire bytes are bit-identical to a
/// `ColUInt64`-backed implementation.
///
/// State header (LowCardinality, …) forwards to keys first, then values —
/// matches ch-go's `proto/col_map.go` field order.
[<Sealed>]
type ColMap<'K, 'V when 'K : equality and 'K : not null>(keys: IColumnOf<'K>, values: IColumnOf<'V>) =
    let mutable offsets : uint64 array = Array.Empty<uint64>()
    let mutable offsetsCount : int = 0

    let ensureOffsets (n: int) =
        if offsets.Length < n then
            Array.Resize(&offsets, max n (max 8 (offsets.Length * 2)))

    member _.Type = "Map(" + keys.Type + ", " + values.Type + ")"
    member _.Rows = offsetsCount

    /// Number of offset entries currently held (one per map row).
    member _.OffsetsCount = offsetsCount
    /// Zero-copy view of the cumulative offsets (UInt64 each).
    /// `Span.[i] = end-of-row-i` in `Keys` / `Values`. Lifetime: valid only
    /// until the next `Append` / `AppendKV` / `DecodeColumn` / `Reset`.
    member _.OffsetsSpan : ReadOnlySpan<uint64> =
        ReadOnlySpan(offsets, 0, offsetsCount)
    /// Flat keys column.
    member _.Keys = keys
    /// Flat values column.
    member _.Values = values

    member _.Reset() =
        offsetsCount <- 0
        keys.Reset()
        values.Reset()

    /// Number of key→value pairs in row i.
    member _.RowLen(i: int) : int =
        let endIdx = int offsets.[i]
        let startIdx = if i = 0 then 0 else int offsets.[i - 1]
        endIdx - startIdx

    /// Materialise row i as a fresh `Dictionary<'K, 'V>`.
    member _.Row(i: int) : Dictionary<'K, 'V> =
        let endIdx = int offsets.[i]
        let startIdx = if i = 0 then 0 else int offsets.[i - 1]
        let length = endIdx - startIdx
        let result = Dictionary<'K, 'V>(length)
        for idx in startIdx .. endIdx - 1 do
            result.[keys.Row(idx)] <- values.Row(idx)
        result

    /// Materialise row i as an ordered `KeyValuePair[]` — preserves wire order,
    /// no Dictionary allocation. Useful when you need stable iteration or when
    /// the inner keys aren't hashable.
    member _.RowKV(i: int) : KeyValuePair<'K, 'V> array =
        let endIdx = int offsets.[i]
        let startIdx = if i = 0 then 0 else int offsets.[i - 1]
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
        ensureOffsets (offsetsCount + 1)
        offsets.[offsetsCount] <- uint64 keys.Rows
        offsetsCount <- offsetsCount + 1

    /// Append a row from an ordered list of KV pairs — preserves insertion
    /// order on the wire.
    member _.AppendKV(kv: KeyValuePair<'K, 'V> array) =
        for pair in kv do
            keys.Append(pair.Key)
            values.Append(pair.Value)
        ensureOffsets (offsetsCount + 1)
        offsets.[offsetsCount] <- uint64 keys.Rows
        offsetsCount <- offsetsCount + 1

    member _.DecodeColumn(r: Reader, n: int) =
        if n = 0 then () else
        ensureOffsets n
        r.ReadFull(MemoryMarshal.AsBytes(offsets.AsSpan(0, n)))
        offsetsCount <- n
        let totalInner = int offsets.[n - 1]
        keys.DecodeColumn(r, totalInner)
        values.DecodeColumn(r, totalInner)

    member _.EncodeColumn(b: Buf) =
        if offsetsCount > 0 then
            b.PutRaw(MemoryMarshal.AsBytes(
                ReadOnlySpan(offsets, 0, offsetsCount)))
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
