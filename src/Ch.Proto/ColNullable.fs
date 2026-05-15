namespace Ch.Proto

open System

/// `Nullable(T)` — wraps an inner column with a per-row null mask.
/// Wire format per block:
///   N bytes  : null mask, 0 = not-null, 1 = null
///   <inner>  : N values from the inner column body
///
/// Nulls storage: raw `byte[]` + count, read/written directly through
/// `r.ReadFull(span)` / `b.PutRaw(span)`. No `MemoryMarshal` cast needed —
/// nulls are already byte-per-row.
///
/// Null rows still occupy a slot in the inner column (filler value).
/// `Row(i)` returns `'T voption` — `ValueNone` for null rows, `ValueSome x`
/// otherwise. ch-go reference: `proto/col_nullable.go`.
[<Sealed>]
type ColNullable<'T>(inner: IColumnOf<'T>) =
    let mutable nulls : byte array = Array.Empty<byte>()
    let mutable nullsCount : int = 0

    let ensureNulls (n: int) =
        if nulls.Length < n then
            Array.Resize(&nulls, max n (max 8 (nulls.Length * 2)))

    member _.Type = "Nullable(" + inner.Type + ")"
    member _.Rows = nullsCount

    /// Number of null-mask bytes currently held (one per row).
    member _.NullsCount = nullsCount
    /// Zero-copy view of the null-mask bytes (0 = not-null, 1 = null).
    /// Lifetime: valid only until the next `Append` / `DecodeColumn` / `Reset`.
    member _.NullsSpan : ReadOnlySpan<byte> =
        ReadOnlySpan(nulls, 0, nullsCount)
    /// Direct access to the values column. Reading at a null index returns
    /// the inner column's filler — usually 0 / "" — not meaningful.
    member _.Inner = inner

    /// Typed view of the inner value buffer when `inner` is bulk-readable
    /// (a `ColPrimitive<'T>`). At null rows the value is the inner's filler
    /// (`Unchecked.defaultof<'T>`) — not meaningful. Combine with `NullsSpan`
    /// to branchless-mask values. Lifetime: aliases inner's buffer.
    /// Raises `NotSupportedException` for non-bulk inner (e.g. `ColStr`).
    /// (Method, not property — and the match returns the interface, not
    /// the span, because `raise : exn -> 'a` can't instantiate `'a` as a
    /// byref-like `ReadOnlySpan<'T>`.)
    member _.ValueSpan() : ReadOnlySpan<'T> =
        let bulk : IBulkReadable<'T> =
            match box inner with
            | :? IBulkReadable<'T> as b -> b
            | _ ->
                raise (NotSupportedException
                    $"ColNullable.ValueSpan requires bulk-readable inner; got {inner.GetType().Name}")
        bulk.AsSpan()

    member _.Reset() =
        nullsCount <- 0
        inner.Reset()

    /// True if row i is null.
    member _.IsNull(i: int) : bool = nulls.[i] = 1uy

    /// Append a maybe-null value. Null rows still push a filler into the
    /// inner column to keep row alignment.
    member _.Append(v: 'T voption) =
        ensureNulls (nullsCount + 1)
        match v with
        | ValueSome x ->
            nulls.[nullsCount] <- 0uy
            inner.Append(x)
        | ValueNone ->
            nulls.[nullsCount] <- 1uy
            inner.Append(Unchecked.defaultof<'T>)
        nullsCount <- nullsCount + 1

    /// Read row i.
    member _.Row(i: int) : 'T voption =
        if nulls.[i] = 1uy then ValueNone
        else ValueSome (inner.Row(i))

    member _.DecodeColumn(r: Reader, n: int) =
        ensureNulls n
        if n > 0 then
            r.ReadFull(nulls.AsSpan(0, n))
        nullsCount <- n
        inner.DecodeColumn(r, n)

    member _.EncodeColumn(b: Buf) =
        if nullsCount > 0 then
            b.PutRaw(ReadOnlySpan(nulls, 0, nullsCount))
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

    /// Recursive composition: `Array(Nullable(T))` → wrap self in a ColArr.
    interface IArrayable with
        member this.Array() =
            ColArr<'T voption>(this :> IColumnOf<'T voption>) :> IColumnResult
