module Ch.Proto.Tests.ColDecimalTests

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

let private parseDecimal (s: string) =
    System.Decimal.Parse(s, System.Globalization.CultureInfo.InvariantCulture)

[<Tests>]
let tests = testList "ColDecimal" [
    testCase "ColDecimal32 50-row sample matches col_decimal32 golden" <| fun _ ->
        let col = ColDecimal32()
        for i in 0 .. 49 do col.Append(int32 i)

        Expect.equal col.Rows 50 "rows"
        Expect.equal col.Type "Decimal32" "type"

        let buf = Buf()
        col.EncodeColumn(buf)
        let expected = File.ReadAllBytes(goldenPath "col_decimal32")
        Expect.equal (buf.WrittenSpan.ToArray()) expected "encoded bytes"

        let dec = ColDecimal32()
        let ms = new MemoryStream(expected)
        dec.DecodeColumn(Reader(ms), 50)
        for i in 0 .. 49 do Expect.equal (dec.Row(i)) (int32 i) "row"

    testCase "ColDecimal64 50-row sample matches col_decimal64 golden" <| fun _ ->
        let col = ColDecimal64()
        for i in 0 .. 49 do col.Append(int64 i)

        Expect.equal col.Rows 50 "rows"
        Expect.equal col.Type "Decimal64" "type"

        let buf = Buf()
        col.EncodeColumn(buf)
        let expected = File.ReadAllBytes(goldenPath "col_decimal64")
        Expect.equal (buf.WrittenSpan.ToArray()) expected "encoded bytes"

        let dec = ColDecimal64()
        let ms = new MemoryStream(expected)
        dec.DecodeColumn(Reader(ms), 50)
        for i in 0 .. 49 do Expect.equal (dec.Row(i)) (int64 i) "row"

    testCase "ColDecimal128 50-row sample matches col_decimal128 golden" <| fun _ ->
        let col = ColDecimal128()
        for i in 0 .. 49 do col.Append(System.Int128(0UL, uint64 i))

        Expect.equal col.Rows 50 "rows"
        Expect.equal col.Type "Decimal128" "type"

        let buf = Buf()
        col.EncodeColumn(buf)
        let expected = File.ReadAllBytes(goldenPath "col_decimal128")
        Expect.equal (buf.WrittenSpan.ToArray()) expected "encoded bytes"

        let dec = ColDecimal128()
        let ms = new MemoryStream(expected)
        dec.DecodeColumn(Reader(ms), 50)
        for i in 0 .. 49 do
            Expect.equal (dec.Row(i)) (System.Int128(0UL, uint64 i)) "row"

    testList "Decimal.fromInt32" [
        for (raw, scale, expected) in
            [ (0, 0, "0"); (1, 0, "1"); (1, 2, "0.01"); (12345, 2, "123.45")
              (-12345, 2, "-123.45"); (100, 4, "0.0100"); (1000000000, 9, "1.000000000") ] ->
            testCase $"raw={raw} scale={scale}" <| fun _ ->
                Expect.equal (Decimal.fromInt32 raw scale) (parseDecimal expected) "scaled"
    ]

    testList "Decimal.toInt32" [
        for (input, scale, expectedRaw) in
            [ ("0", 0, 0); ("123.45", 2, 12345); ("-123.45", 2, -12345); ("3.14", 2, 314) ] ->
            testCase $"input={input} scale={scale}" <| fun _ ->
                let d = parseDecimal input
                let raw = Decimal.toInt32 d scale
                Expect.equal raw expectedRaw "raw"
                Expect.equal (Decimal.fromInt32 raw scale) d "roundtrip"
    ]

    testList "Decimal.fromInt64" [
        for (raw, scale, expected) in
            [ (0L, 0, "0")
              (123456789012345L, 6, "123456789.012345")
              (-123456789012345L, 6, "-123456789.012345") ] ->
            testCase $"raw={raw}" <| fun _ ->
                Expect.equal (Decimal.fromInt64 raw scale) (parseDecimal expected) "scaled"
    ]

    testList "ColumnType.normalize Decimal" [
        for (input, expected) in
            [ "Decimal(9, 2)", "Decimal32"
              "Decimal(9,2)", "Decimal32"
              "Decimal(18, 4)", "Decimal64"
              "Decimal(38, 8)", "Decimal128"
              "Decimal(76, 0)", "Decimal256"
              "Array(Decimal(9, 2))", "Array(Decimal32)"
              "Nullable(Decimal(18, 4))", "Nullable(Decimal64)"
              "Map(String, Decimal(9, 2))", "Map(String, Decimal32)"
              "Int32", "Int32"
              "String", "String" ] ->
            testCase $"normalize {input}" <| fun _ ->
                Expect.equal (ColumnType.normalize input) expected "normalized"
    ]

    testList "ColumnType.isCompatible" [
        for (client, server, expected) in
            [ "Decimal32", "Decimal(9, 2)", true
              "Decimal64", "Decimal(18, 4)", true
              "Decimal128", "Decimal(38, 0)", true
              "Decimal32", "Decimal(19, 0)", false
              "Int32", "Int32", true
              "Int32", "Int64", false
              "Array(Decimal32)", "Array(Decimal(9, 4))", true ] ->
            testCase $"{client} ↔ {server}" <| fun _ ->
                Expect.equal (ColumnType.isCompatible client server) expected "compat"
    ]
]
