namespace Ch.Proto

open System

/// `Nullable(T)` — wraps an inner column with a per-row null mask.
/// Wire format per block:
///   N bytes  : null mask, 0 = not-null, 1 = null
///   <inner>  : N values from the inner column body
///
/// Null rows still occupy a slot in the inner column (filler value).
/// `Row(i)` returns `'T voption` — `ValueNone` for null rows, `ValueSome x`
/// otherwise. ch-go reference: `proto/col_nullable.go`.
[<Sealed>]
type ColNullable<'T>(inner: IColumnOf<'T>) =
    let nulls = ColUInt8()

    member _.Type = "Nullable(" + inner.Type + ")"
    member _.Rows = nulls.Rows

    /// Direct access to the underlying null-mask column (1 byte per row).
    member _.Nulls = nulls
    /// Direct access to the values column. Reading at a null index returns
    /// the inner column's filler — usually 0 / "" — not meaningful.
    member _.Inner = inner

    member _.Reset() =
        nulls.Reset()
        inner.Reset()

    /// True if row i is null.
    member _.IsNull(i: int) : bool = nulls.Row(i) = 1uy

    /// Append a maybe-null value. Null rows still push a filler into the
    /// inner column to keep row alignment.
    member _.Append(v: 'T voption) =
        match v with
        | ValueSome x ->
            nulls.Append(0uy)
            inner.Append(x)
        | ValueNone ->
            nulls.Append(1uy)
            inner.Append(Unchecked.defaultof<'T>)

    /// Read row i.
    member _.Row(i: int) : 'T voption =
        if nulls.Row(i) = 1uy then ValueNone
        else ValueSome (inner.Row(i))

    member _.DecodeColumn(r: Reader, n: int) =
        nulls.DecodeColumn(r, n)
        inner.DecodeColumn(r, n)

    member _.EncodeColumn(b: Buf) =
        nulls.EncodeColumn(b)
        inner.EncodeColumn(b)

    interface IColumnResult with
        member this.Type = this.Type
        member this.Rows = this.Rows
        member this.Reset() = this.Reset()
        member this.DecodeColumn(r, n) = this.DecodeColumn(r, n)
        member this.EncodeColumn(b) = this.EncodeColumn(b)

    interface IColumnOf<'T voption> with
        member this.Append(v) = this.Append(v)
        member this.Row(i) = this.Row(i)
