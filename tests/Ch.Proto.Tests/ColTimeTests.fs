module Ch.Proto.Tests.ColTimeTests

open System
open System.IO
open Xunit
open Ch.Proto

let private goldenPath (name: string) : string =
    let p =
        Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                "reference", "ch-go", "proto", "_golden",
                $"{name}.raw"))
    if not (File.Exists p) then
        failwith $"golden fixture not found: {p}"
    p

let private wireRoundtrip<'T
        when 'T : unmanaged
        and 'T : struct
        and 'T : (new : unit -> 'T)
        and 'T :> ValueType
        and 'T : equality>
        (newCol: unit -> ColPrimitive<'T>)
        (rows: int)
        (mkValue: int -> 'T)
        (goldenName: string)
        (expectedTypeName: string) =
    let col = newCol()
    for i in 0 .. rows - 1 do
        col.Append(mkValue i)
    Assert.Equal(rows, col.Rows)
    Assert.Equal(expectedTypeName, col.Type)
    let buf = Buf()
    col.EncodeColumn(buf)
    let expected = File.ReadAllBytes(goldenPath goldenName)
    Assert.Equal<byte array>(expected, buf.WrittenSpan.ToArray())
    let ms = new MemoryStream(expected)
    let r = Reader(ms)
    let dec = newCol()
    dec.DecodeColumn(r, rows)
    for i in 0 .. rows - 1 do
        Assert.Equal<'T>(mkValue i, dec.Row(i))

[<Fact>]
let ``ColDate wire roundtrips against col_date golden`` () =
    wireRoundtrip (fun () -> ColDate() :> ColPrimitive<uint16>) 50 (fun i -> uint16 i) "col_date" "Date"

[<Fact>]
let ``ColDate32 wire roundtrips against col_date32 golden`` () =
    wireRoundtrip (fun () -> ColDate32() :> ColPrimitive<int32>) 50 (fun i -> int32 i) "col_date32" "Date32"

[<Fact>]
let ``ColDateTime wire roundtrips against col_datetime golden`` () =
    wireRoundtrip (fun () -> ColDateTime() :> ColPrimitive<uint32>) 50 (fun i -> uint32 i) "col_datetime" "DateTime"

[<Fact>]
let ``ColDateTime64 wire roundtrips against col_datetime64 golden (no precision)`` () =
    // ch-go's golden was generated with the default (no precision set).
    wireRoundtrip (fun () -> ColDateTime64() :> ColPrimitive<int64>) 50 (fun i -> int64 i) "col_datetime64" "DateTime64"

[<Fact>]
let ``ColIPv4 wire roundtrips against col_ipv4 golden`` () =
    wireRoundtrip (fun () -> ColIPv4() :> ColPrimitive<uint32>) 50 (fun i -> uint32 i) "col_ipv4" "IPv4"

// ─── .NET-side semantic round-trips ───

[<Fact>]
let ``ColDate AppendDate / RowDate round-trips`` () =
    let col = ColDate()
    // ClickHouse Date upper bound is 2149-06-06 (day 65535 = uint16 max).
    let days = [ DateOnly(1970, 1, 1); DateOnly(2024, 5, 13); DateOnly(2149, 6, 6) ]
    for d in days do col.AppendDate(d)
    for i, d in List.indexed days do
        Assert.Equal(d, col.RowDate(i))

[<Fact>]
let ``ColDate32 AppendDate / RowDate handles pre-1970`` () =
    let col = ColDate32()
    let days = [ DateOnly(1900, 1, 1); DateOnly(1970, 1, 1); DateOnly(2024, 5, 13); DateOnly(2299, 12, 31) ]
    for d in days do col.AppendDate(d)
    for i, d in List.indexed days do
        Assert.Equal(d, col.RowDate(i))

[<Fact>]
let ``ColDateTime AppendDateTime / RowDateTime round-trips seconds`` () =
    let col = ColDateTime()
    let dt = DateTime(2024, 5, 13, 12, 34, 56, DateTimeKind.Utc)
    col.AppendDateTime(dt)
    let got = col.RowDateTime(0)
    Assert.Equal(dt, got)

[<Fact>]
let ``ColDateTime64(3) AppendDateTime preserves milliseconds`` () =
    let col = ColDateTime64(3)
    let dt = DateTime(2024, 5, 13, 12, 34, 56, 789, DateTimeKind.Utc)
    col.AppendDateTime(dt)
    let got = col.RowDateTime(0)
    Assert.Equal(dt, got)
    Assert.Equal("DateTime64(3)", col.Type)

[<Fact>]
let ``ColDateTime64(7) AppendDateTime preserves .NET ticks`` () =
    let col = ColDateTime64(7)
    // .NET ticks = 100 ns, precision 7.
    let dt = DateTime(2024, 5, 13, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234567L)
    col.AppendDateTime(dt)
    Assert.Equal(dt, col.RowDateTime(0))

[<Fact>]
let ``ColIPv4 AppendIP / RowIP round-trips`` () =
    let col = ColIPv4()
    let ip = System.Net.IPAddress.Parse("192.168.1.42")
    col.AppendIP(ip)
    Assert.Equal(ip, col.RowIP(0))
