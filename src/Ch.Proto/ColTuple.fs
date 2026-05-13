namespace Ch.Proto

open System

/// Wraps a typed column with a field name so it can carry into a `ColTuple`'s
/// `Tuple(name1 T1, name2 T2, …)` type string. The wire format is identical
/// to the inner column — only the Type string changes. ch-go reference:
/// `proto/col_tuple.go: ColNamed`.
[<Sealed>]
type ColNamed<'T>(name: string, inner: IColumnOf<'T>) =
    member _.Name = name
    member _.Inner = inner

    member _.Type = name + " " + inner.Type
    member _.Rows = inner.Rows
    member _.Reset() = inner.Reset()
    member _.DecodeColumn(r, n) = inner.DecodeColumn(r, n)
    member _.EncodeColumn(b) = inner.EncodeColumn(b)
    member _.Append(v: 'T) = inner.Append(v)
    member _.Row(i: int) : 'T = inner.Row(i)

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
        member _.DecodeState(r) =
            match inner with
            | :? IStatefulColumn as s -> s.DecodeState(r)
            | _ -> ()
        member _.EncodeState(b) =
            match inner with
            | :? IStatefulColumn as s -> s.EncodeState(b)
            | _ -> ()


/// `Tuple(T1, T2, …)` — a heterogeneous group of inner columns. The wire
/// format is just each inner column's body in order, no offsets or null
/// mask of its own. All inners share the same row count.
///
/// Heterogeneous, so we can't surface a single `IColumnOf<'T>` — typed
/// per-row access is via the original (typed) inner column references the
/// caller passed in. Use `Inners.[i]` for index-based access if you only
/// have the tuple in hand.
///
/// State (LowCardinality, …) forwards to each inner in order. ch-go
/// reference: `proto/col_tuple.go`.
[<Sealed>]
type ColTuple(inners: IColumnResult array) =
    member _.Inners = inners

    member _.Type =
        let parts = inners |> Array.map (fun c -> c.Type)
        "Tuple(" + String.concat ", " parts + ")"

    member _.Rows =
        if inners.Length = 0 then 0 else inners.[0].Rows

    member _.Reset() =
        for c in inners do c.Reset()

    member _.DecodeColumn(r: Reader, n: int) =
        for c in inners do c.DecodeColumn(r, n)

    member _.EncodeColumn(b: Buf) =
        for c in inners do c.EncodeColumn(b)

    member _.DecodeState(r: Reader) =
        for c in inners do
            match c with
            | :? IStatefulColumn as s -> s.DecodeState(r)
            | _ -> ()

    member _.EncodeState(b: Buf) =
        for c in inners do
            match c with
            | :? IStatefulColumn as s -> s.EncodeState(b)
            | _ -> ()

    interface IColumnResult with
        member this.Type = this.Type
        member this.Rows = this.Rows
        member this.Reset() = this.Reset()
        member this.DecodeColumn(r, n) = this.DecodeColumn(r, n)
        member this.EncodeColumn(b) = this.EncodeColumn(b)

    interface IStatefulColumn with
        member this.DecodeState(r) = this.DecodeState(r)
        member this.EncodeState(b) = this.EncodeState(b)
