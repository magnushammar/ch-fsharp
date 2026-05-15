namespace Ch.Proto

open System

/// A 2-D point — paired Float64 coordinates. Matches ch-go's
/// `proto.Point{X, Y}`. Struct so the value type round-trips through generic
/// composites (`Array(Point)`, `Nullable(Point)`) without boxing.
[<Struct>]
type Point = { X: float; Y: float }

/// `Point` — ClickHouse Geo type stored on the wire as two parallel
/// Float64 columns (no offsets). Wire layout is identical to
/// `Tuple(Float64, Float64)` but the type string is `Point`. ch-go
/// reference: `proto/col_point.go`.
[<Sealed>]
type ColPoint() =
    let x = ColFloat64()
    let y = ColFloat64()

    member _.Type = "Point"
    member _.Rows = x.Rows

    /// X-coordinate column (Float64).
    member _.X = x
    /// Y-coordinate column (Float64).
    member _.Y = y

    member _.Reset() =
        x.Reset()
        y.Reset()

    member _.Append(p: Point) =
        x.Append(p.X)
        y.Append(p.Y)

    member _.Append(px: float, py: float) =
        x.Append(px)
        y.Append(py)

    member _.Row(i: int) : Point =
        { X = x.Row(i); Y = y.Row(i) }

    member _.DecodeColumn(r: Reader, n: int) =
        x.DecodeColumn(r, n)
        y.DecodeColumn(r, n)

    member _.EncodeColumn(b: Buf) =
        x.EncodeColumn(b)
        y.EncodeColumn(b)

    interface IColumnResult with
        member this.Type = this.Type
        member this.Rows = this.Rows
        member this.Reset() = this.Reset()
        member this.DecodeColumn(r, n) = this.DecodeColumn(r, n)
        member this.EncodeColumn(b) = this.EncodeColumn(b)

    interface IColumnOf<Point> with
        member this.Append(v) = this.Append(v)
        member this.Row(i) = this.Row(i)

    interface IArrayable with
        member this.Array() = ColArr<Point>(this) :> IColumnResult

    interface INullable with
        member this.Nullable() = ColNullable<Point>(this) :> IColumnResult

    interface ILowCardinality with
        member this.LowCardinality() = ColLowCardinality<Point>(this) :> IColumnResult
