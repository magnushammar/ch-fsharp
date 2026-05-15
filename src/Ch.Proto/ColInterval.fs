namespace Ch.Proto

open System

/// Unit of an `Interval` value. ClickHouse type names are `IntervalSecond`,
/// `IntervalMinute`, … `IntervalYear`. Newer ClickHouse versions also expose
/// sub-second scales — those are out of scope here, matching ch-go.
type IntervalScale =
    | Second
    | Minute
    | Hour
    | Day
    | Week
    | Month
    | Quarter
    | Year

module IntervalScale =
    let toTypeString (s: IntervalScale) : string =
        match s with
        | Second -> "IntervalSecond"
        | Minute -> "IntervalMinute"
        | Hour -> "IntervalHour"
        | Day -> "IntervalDay"
        | Week -> "IntervalWeek"
        | Month -> "IntervalMonth"
        | Quarter -> "IntervalQuarter"
        | Year -> "IntervalYear"

    let fromTypeString (t: string) : IntervalScale =
        match t with
        | "IntervalSecond" -> Second
        | "IntervalMinute" -> Minute
        | "IntervalHour" -> Hour
        | "IntervalDay" -> Day
        | "IntervalWeek" -> Week
        | "IntervalMonth" -> Month
        | "IntervalQuarter" -> Quarter
        | "IntervalYear" -> Year
        | other -> raise (FormatException $"unknown interval type: '{other}'")

/// A ClickHouse `Interval` value: scale + signed 64-bit magnitude. Struct so
/// it round-trips through composites without boxing.
[<Struct>]
type Interval = { Scale: IntervalScale; Value: int64 }

/// `Interval{Scale}` — Int64 wire format with a fixed scale unit. The scale
/// is fixed per column (the wire is a flat Int64 sequence; the unit comes
/// from the type string). Use `Infer` to set the scale from the server-sent
/// type, or pass it to the constructor.
/// ch-go reference: `proto/col_interval.go`.
[<Sealed>]
type ColInterval() =
    let raw = ColInt64()
    let mutable scale = Second

    new (s: IntervalScale) as this =
        ColInterval()
        then this.SetScale(s)

    member private _.SetScale(s: IntervalScale) = scale <- s

    member _.Type = IntervalScale.toTypeString scale
    member _.Rows = raw.Rows
    member _.Scale = scale
    /// Underlying Int64 column.
    member _.Raw = raw

    member _.Reset() = raw.Reset()

    /// Append an interval. The scale must match the column's scale.
    member _.Append(v: Interval) =
        if v.Scale <> scale then
            raise (ArgumentException(
                $"can't append {v.Scale} interval to {scale} column", "v"))
        raw.Append(v.Value)

    /// Append a raw magnitude with the column's pre-set scale.
    member _.AppendValue(value: int64) = raw.Append(value)

    member _.Row(i: int) : Interval = { Scale = scale; Value = raw.Row(i) }
    member _.RawValue(i: int) : int64 = raw.Row(i)

    member _.DecodeColumn(r: Reader, n: int) = raw.DecodeColumn(r, n)
    member _.EncodeColumn(b: Buf) = raw.EncodeColumn(b)

    member this.Infer(t: string) =
        scale <- IntervalScale.fromTypeString t

    interface IColumnResult with
        member this.Type = this.Type
        member this.Rows = this.Rows
        member this.Reset() = this.Reset()
        member this.DecodeColumn(r, n) = this.DecodeColumn(r, n)
        member this.EncodeColumn(b) = this.EncodeColumn(b)

    interface IColumnOf<Interval> with
        member this.Append(v) = this.Append(v)
        member this.Row(i) = this.Row(i)

    interface IInferable with
        member this.Infer(t) = this.Infer(t)

    interface IArrayable with
        member this.Array() = ColArr<Interval>(this) :> IColumnResult

    interface INullable with
        member this.Nullable() = ColNullable<Interval>(this) :> IColumnResult

    interface ILowCardinality with
        member this.LowCardinality() = ColLowCardinality<Interval>(this) :> IColumnResult
