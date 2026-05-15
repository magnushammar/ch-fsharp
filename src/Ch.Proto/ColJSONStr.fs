namespace Ch.Proto

open System

/// `JSON` — wire-identical to `String` (length-prefixed UTF-8 body) but with
/// a per-block state header carrying the serialization-version UInt64.
/// Reading requires `output_format_native_write_json_as_string = 1` on the
/// server / settings side (older serialization formats fail the version
/// check). ch-go reference: `proto/col_json_str.go`.
[<Sealed>]
type ColJSONStr() =
    let inner = ColStr()
    static let serializationVersion = 1UL

    member _.Type = "JSON"
    member _.Rows = inner.Rows

    /// Underlying String column for users who need byte-level access.
    member _.Inner = inner

    member _.Reset() = inner.Reset()
    member _.Append(s: string) = inner.Append(s)
    member _.Row(i: int) : string = inner.Row(i)

    member _.DecodeColumn(r: Reader, n: int) = inner.DecodeColumn(r, n)
    member _.EncodeColumn(b: Buf) = inner.EncodeColumn(b)

    member _.DecodeState(r: Reader) =
        let v = r.UInt64()
        if v <> serializationVersion then
            raise (FormatException(
                $"unexpected JSON serialization version {v}; "
                + "set output_format_native_write_json_as_string=1"))

    member _.EncodeState(b: Buf) = b.PutUInt64(serializationVersion)

    interface IColumnResult with
        member this.Type = this.Type
        member this.Rows = this.Rows
        member this.Reset() = this.Reset()
        member this.DecodeColumn(r, n) = this.DecodeColumn(r, n)
        member this.EncodeColumn(b) = this.EncodeColumn(b)

    interface IColumnOf<string> with
        member this.Append(v) = this.Append(v)
        member this.Row(i) = this.Row(i)

    interface IStatefulColumn with
        member this.DecodeState(r) = this.DecodeState(r)
        member this.EncodeState(b) = this.EncodeState(b)

    interface IArrayable with
        member this.Array() = ColArr<string>(this) :> IColumnResult

    interface INullable with
        member this.Nullable() = ColNullable<string>(this) :> IColumnResult

    interface ILowCardinality with
        member this.LowCardinality() = ColLowCardinality<string>(this) :> IColumnResult
