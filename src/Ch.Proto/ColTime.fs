namespace Ch.Proto

open System

/// `Date` — UInt16 days since the Unix epoch (1970-01-01).
/// Wire format identical to ColUInt16; only the Type string differs.
[<Sealed>]
type ColDate() =
    inherit ColPrimitive<uint16>("Date")
    static let epoch = DateOnly(1970, 1, 1)
    member this.AppendDate(d: DateOnly) =
        this.Append(uint16 (d.DayNumber - epoch.DayNumber))
    member this.RowDate(i: int) : DateOnly =
        epoch.AddDays(int (this.Row(i)))

/// `Date32` — signed Int32 days since 1970-01-01 (range goes back to 1900).
[<Sealed>]
type ColDate32() =
    inherit ColPrimitive<int32>("Date32")
    static let epoch = DateOnly(1970, 1, 1)
    member this.AppendDate(d: DateOnly) =
        this.Append(int32 (d.DayNumber - epoch.DayNumber))
    member this.RowDate(i: int) : DateOnly =
        epoch.AddDays(this.Row(i))

/// `DateTime` — UInt32 seconds since the Unix epoch (UTC).
[<Sealed>]
type ColDateTime() =
    inherit ColPrimitive<uint32>("DateTime")
    static let epoch = DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    member this.AppendDateTime(dt: DateTime) =
        let utc = if dt.Kind = DateTimeKind.Utc then dt else dt.ToUniversalTime()
        this.Append(uint32 ((utc - epoch).TotalSeconds))
    member this.RowDateTime(i: int) : DateTime =
        epoch.AddSeconds(float (this.Row(i)))

/// `DateTime64(N)` — signed Int64 with sub-second precision (N digits, 0..9).
/// Wire format identical to ColInt64; precision is encoded only in the
/// type string. Precision 7 maps cleanly to .NET ticks.
[<Sealed>]
type ColDateTime64 private (precision: int, typeName: string) =
    inherit ColPrimitive<int64>(typeName)
    static let epoch = DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    let ticksPerUnit =
        // .NET DateTime ticks are 100ns = precision 7. For other precisions
        // we scale to .NET ticks and round.
        if precision <= 7 then pown 10L (7 - precision)
        else 0L

    /// Default constructor: no precision set, Type = "DateTime64".
    /// Used for round-trip wire tests against ch-go where Precision is unset.
    new() = ColDateTime64(-1, "DateTime64")

    /// Specific precision, Type = "DateTime64(N)".
    new(precision: int) =
        if precision < 0 || precision > 9 then
            raise (ArgumentOutOfRangeException("precision", "must be 0..9"))
        ColDateTime64(precision, sprintf "DateTime64(%d)" precision)

    /// Precision (0..9) or -1 if unset.
    member _.Precision = precision

    member this.AppendDateTime(dt: DateTime) =
        if precision < 0 then
            raise (InvalidOperationException "ColDateTime64 precision not set; use ColDateTime64(N)")
        let utc = if dt.Kind = DateTimeKind.Utc then dt else dt.ToUniversalTime()
        let deltaTicks = (utc - epoch).Ticks
        let value =
            if precision <= 7 then deltaTicks / ticksPerUnit
            else deltaTicks * pown 10L (precision - 7)
        this.Append(value)

    member this.RowDateTime(i: int) : DateTime =
        if precision < 0 then
            raise (InvalidOperationException "ColDateTime64 precision not set; use ColDateTime64(N)")
        let value = this.Row(i)
        let ticks =
            if precision <= 7 then value * ticksPerUnit
            else value / pown 10L (precision - 7)
        epoch.AddTicks(ticks)

/// `IPv4` — UInt32 (little-endian on the wire), but interpreted as a 32-bit IP.
[<Sealed>]
type ColIPv4() =
    inherit ColPrimitive<uint32>("IPv4")
    member this.AppendIP(addr: System.Net.IPAddress) =
        let bytes = addr.GetAddressBytes()
        if bytes.Length <> 4 then
            raise (ArgumentException("not an IPv4 address", "addr"))
        // IPAddress.GetAddressBytes returns network-byte-order (BE). ClickHouse
        // stores IPv4 as little-endian UInt32 on the wire, so we reverse.
        let u =
            (uint32 bytes.[0])
            ||| ((uint32 bytes.[1]) <<< 8)
            ||| ((uint32 bytes.[2]) <<< 16)
            ||| ((uint32 bytes.[3]) <<< 24)
        this.Append(u)
    member this.RowIP(i: int) : System.Net.IPAddress =
        let u = this.Row(i)
        let bytes : byte array = [|
            byte (u &&& 0xFFu)
            byte ((u >>> 8) &&& 0xFFu)
            byte ((u >>> 16) &&& 0xFFu)
            byte ((u >>> 24) &&& 0xFFu)
        |]
        System.Net.IPAddress(bytes)
