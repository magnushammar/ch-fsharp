namespace Ch.Proto

open System

/// Singleton tag for ClickHouse's `Nothing` type. ch-go calls this
/// `proto.Nothing`. Empty struct — value-typed so it round-trips through
/// generic composites without boxing.
[<Struct>]
type Nothing = struct end

/// ClickHouse's `Nothing` type — appears in `SELECT NULL` (typed as
/// `Nullable(Nothing)`) and as the inner of an all-NULL Array/Map. One byte
/// per row on the wire, content ignored. ch-go reference:
/// `proto/col_nothing.go`.
///
/// `Append`/`Row` take `Nothing`. Use `AppendN(n)` for bulk extension.
[<Sealed>]
type ColNothing() =
    let mutable count : int = 0

    member _.Type = "Nothing"
    member _.Rows = count

    member _.Reset() = count <- 0

    member _.Append(_: Nothing) = count <- count + 1

    member _.AppendN(n: int) = count <- count + n

    member _.Row(_: int) : Nothing = Nothing()

    member _.DecodeColumn(r: Reader, n: int) =
        if n > 0 then
            // Server pads with n bytes (content is meaningless); skip them.
            let buf : byte array = Array.zeroCreate n
            r.ReadFull(buf.AsSpan(0, n))
        count <- n

    member _.EncodeColumn(b: Buf) =
        if count > 0 then
            // Wire is n zero bytes — content is ignored, just placeholder.
            let buf : byte array = Array.zeroCreate count
            b.PutRaw(ReadOnlySpan(buf, 0, count))

    interface IColumnResult with
        member this.Type = this.Type
        member this.Rows = this.Rows
        member this.Reset() = this.Reset()
        member this.DecodeColumn(r, n) = this.DecodeColumn(r, n)
        member this.EncodeColumn(b) = this.EncodeColumn(b)

    interface IColumnOf<Nothing> with
        member this.Append(v) = this.Append(v)
        member this.Row(i) = this.Row(i)
