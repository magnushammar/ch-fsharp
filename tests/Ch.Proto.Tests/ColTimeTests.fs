module Ch.Proto.Tests.ColTimeTests

open System
open System.IO
open Expecto
open Ch.Proto

let private goldenPath (name: string) : string =
    let p =
        Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                "reference", "ch-go", "proto", "_golden",
                $"{name}.raw"))
    if not (File.Exists p) then failwithf "golden fixture not found: %s" p
    p

let private wireRoundtrip<'T
        when 'T : unmanaged
        and 'T : struct
        and 'T : (new : unit -> 'T)
        and 'T :> ValueType
        and 'T : equality
        and 'T : not null>
        (newCol: unit -> ColPrimitive<'T>)
        (rows: int)
        (mkValue: int -> 'T)
        (goldenName: string)
        (expectedTypeName: string) =
    let col = newCol()
    for i in 0 .. rows - 1 do col.Append(mkValue i)
    Expect.equal col.Rows rows "rows"
    Expect.equal col.Type expectedTypeName "type"
    let buf = Buf()
    col.EncodeColumn(buf)
    let expected = File.ReadAllBytes(goldenPath goldenName)
    Expect.equal (buf.WrittenSpan.ToArray()) expected "encoded bytes"
    let ms = new MemoryStream(expected)
    let r = Reader(ms)
    let dec = newCol()
    dec.DecodeColumn(r, rows)
    for i in 0 .. rows - 1 do
        Expect.equal (dec.Row(i)) (mkValue i) "decoded row"

[<Tests>]
let tests = testList "ColTime" [
    testCase "ColDate wire roundtrips against col_date golden" <| fun _ ->
        wireRoundtrip (fun () -> ColDate() :> ColPrimitive<uint16>) 50 (fun i -> uint16 i) "col_date" "Date"

    testCase "ColDate32 wire roundtrips against col_date32 golden" <| fun _ ->
        wireRoundtrip (fun () -> ColDate32() :> ColPrimitive<int32>) 50 (fun i -> int32 i) "col_date32" "Date32"

    testCase "ColDateTime wire roundtrips against col_datetime golden" <| fun _ ->
        wireRoundtrip (fun () -> ColDateTime() :> ColPrimitive<uint32>) 50 (fun i -> uint32 i) "col_datetime" "DateTime"

    testCase "ColDateTime64 wire roundtrips against col_datetime64 golden (no precision)" <| fun _ ->
        wireRoundtrip (fun () -> ColDateTime64() :> ColPrimitive<int64>) 50 (fun i -> int64 i) "col_datetime64" "DateTime64"

    testCase "ColIPv4 wire roundtrips against col_ipv4 golden" <| fun _ ->
        wireRoundtrip (fun () -> ColIPv4() :> ColPrimitive<uint32>) 50 (fun i -> uint32 i) "col_ipv4" "IPv4"

    testCase "ColDate AppendDate / RowDate round-trips" <| fun _ ->
        let col = ColDate()
        let days = [ DateOnly(1970, 1, 1); DateOnly(2024, 5, 13); DateOnly(2149, 6, 6) ]
        for d in days do col.AppendDate(d)
        for i, d in List.indexed days do Expect.equal (col.RowDate(i)) d "date row"

    testCase "ColDate32 AppendDate / RowDate handles pre-1970" <| fun _ ->
        let col = ColDate32()
        let days = [ DateOnly(1900, 1, 1); DateOnly(1970, 1, 1); DateOnly(2024, 5, 13); DateOnly(2299, 12, 31) ]
        for d in days do col.AppendDate(d)
        for i, d in List.indexed days do Expect.equal (col.RowDate(i)) d "date row"

    testCase "ColDateTime AppendDateTime / RowDateTime round-trips seconds" <| fun _ ->
        let col = ColDateTime()
        let dt = DateTime(2024, 5, 13, 12, 34, 56, DateTimeKind.Utc)
        col.AppendDateTime(dt)
        Expect.equal (col.RowDateTime(0)) dt "dt"

    testCase "ColDateTime64(3) AppendDateTime preserves milliseconds" <| fun _ ->
        let col = ColDateTime64(3)
        let dt = DateTime(2024, 5, 13, 12, 34, 56, 789, DateTimeKind.Utc)
        col.AppendDateTime(dt)
        Expect.equal (col.RowDateTime(0)) dt "dt"
        Expect.equal col.Type "DateTime64(3)" "type"

    testCase "ColDateTime64(7) AppendDateTime preserves .NET ticks" <| fun _ ->
        let col = ColDateTime64(7)
        let dt = DateTime(2024, 5, 13, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234567L)
        col.AppendDateTime(dt)
        Expect.equal (col.RowDateTime(0)) dt "dt"

    testCase "ColIPv4 AppendIP / RowIP round-trips" <| fun _ ->
        let col = ColIPv4()
        let ip = System.Net.IPAddress.Parse("192.168.1.42")
        col.AppendIP(ip)
        Expect.equal (col.RowIP(0)) ip "ip"
]
