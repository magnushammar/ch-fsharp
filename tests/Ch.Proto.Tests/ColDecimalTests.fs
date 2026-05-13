module Ch.Proto.Tests.ColDecimalTests

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

/// Port of `proto/col_decimal32_gen_test.go: TestColDecimal32_DecodeColumn`.
/// 50 rows, value i in Int32 LE.
[<Fact>]
let ``ColDecimal32 50-row sample matches col_decimal32 golden`` () =
    let col = ColDecimal32()
    for i in 0 .. 49 do col.Append(int32 i)

    Assert.Equal(50, col.Rows)
    Assert.Equal("Decimal32", col.Type)

    let buf = Buf()
    col.EncodeColumn(buf)
    let expected = File.ReadAllBytes(goldenPath "col_decimal32")
    Assert.Equal<byte array>(expected, buf.WrittenSpan.ToArray())

    let dec = ColDecimal32()
    let ms = new MemoryStream(expected)
    dec.DecodeColumn(Reader(ms), 50)
    for i in 0 .. 49 do
        Assert.Equal(int32 i, dec.Row(i))

/// Port of `TestColDecimal64_DecodeColumn`. 50 rows in Int64 LE.
[<Fact>]
let ``ColDecimal64 50-row sample matches col_decimal64 golden`` () =
    let col = ColDecimal64()
    for i in 0 .. 49 do col.Append(int64 i)

    Assert.Equal(50, col.Rows)
    Assert.Equal("Decimal64", col.Type)

    let buf = Buf()
    col.EncodeColumn(buf)
    let expected = File.ReadAllBytes(goldenPath "col_decimal64")
    Assert.Equal<byte array>(expected, buf.WrittenSpan.ToArray())

    let dec = ColDecimal64()
    let ms = new MemoryStream(expected)
    dec.DecodeColumn(Reader(ms), 50)
    for i in 0 .. 49 do
        Assert.Equal(int64 i, dec.Row(i))

/// Port of `TestColDecimal128_DecodeColumn`. 50 rows in Int128 LE.
[<Fact>]
let ``ColDecimal128 50-row sample matches col_decimal128 golden`` () =
    let col = ColDecimal128()
    for i in 0 .. 49 do col.Append(System.Int128(0UL, uint64 i))

    Assert.Equal(50, col.Rows)
    Assert.Equal("Decimal128", col.Type)

    let buf = Buf()
    col.EncodeColumn(buf)
    let expected = File.ReadAllBytes(goldenPath "col_decimal128")
    Assert.Equal<byte array>(expected, buf.WrittenSpan.ToArray())

    let dec = ColDecimal128()
    let ms = new MemoryStream(expected)
    dec.DecodeColumn(Reader(ms), 50)
    for i in 0 .. 49 do
        Assert.Equal(System.Int128(0UL, uint64 i), dec.Row(i))

[<Theory>]
[<InlineData(0, 0, "0")>]
[<InlineData(1, 0, "1")>]
[<InlineData(1, 2, "0.01")>]
[<InlineData(12345, 2, "123.45")>]
[<InlineData(-12345, 2, "-123.45")>]
[<InlineData(100, 4, "0.0100")>]
[<InlineData(1000000000, 9, "1.000000000")>]
let ``Decimal.fromInt32 produces the right scaled decimal`` (raw: int32, scale: int, expected: string) =
    let actual = Decimal.fromInt32 raw scale
    Assert.Equal(System.Decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), actual)

[<Theory>]
[<InlineData("0", 0, 0)>]
[<InlineData("123.45", 2, 12345)>]
[<InlineData("-123.45", 2, -12345)>]
[<InlineData("3.14", 2, 314)>]
let ``Decimal.toInt32 round-trips via fromInt32`` (input: string, scale: int, expectedRaw: int32) =
    let d = System.Decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture)
    let raw = Decimal.toInt32 d scale
    Assert.Equal(expectedRaw, raw)
    Assert.Equal(d, Decimal.fromInt32 raw scale)

[<Theory>]
[<InlineData(0L, 0, "0")>]
[<InlineData(123456789012345L, 6, "123456789.012345")>]
[<InlineData(-123456789012345L, 6, "-123456789.012345")>]
let ``Decimal.fromInt64 produces the right scaled decimal`` (raw: int64, scale: int, expected: string) =
    let actual = Decimal.fromInt64 raw scale
    Assert.Equal(System.Decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), actual)

[<Theory>]
[<InlineData("Decimal(9, 2)", "Decimal32")>]
[<InlineData("Decimal(9,2)", "Decimal32")>]
[<InlineData("Decimal(18, 4)", "Decimal64")>]
[<InlineData("Decimal(38, 8)", "Decimal128")>]
[<InlineData("Decimal(76, 0)", "Decimal256")>]
[<InlineData("Array(Decimal(9, 2))", "Array(Decimal32)")>]
[<InlineData("Nullable(Decimal(18, 4))", "Nullable(Decimal64)")>]
[<InlineData("Map(String, Decimal(9, 2))", "Map(String, Decimal32)")>]
[<InlineData("Int32", "Int32")>]
[<InlineData("String", "String")>]
let ``ColumnType.normalize downcasts Decimal(P, S) recursively`` (input: string, expected: string) =
    Assert.Equal(expected, ColumnType.normalize input)

[<Theory>]
[<InlineData("Decimal32", "Decimal(9, 2)", true)>]
[<InlineData("Decimal64", "Decimal(18, 4)", true)>]
[<InlineData("Decimal128", "Decimal(38, 0)", true)>]
[<InlineData("Decimal32", "Decimal(19, 0)", false)>]
[<InlineData("Int32", "Int32", true)>]
[<InlineData("Int32", "Int64", false)>]
[<InlineData("Array(Decimal32)", "Array(Decimal(9, 4))", true)>]
let ``ColumnType.isCompatible accepts equivalent Decimal forms`` (client: string, server: string, expected: bool) =
    Assert.Equal(expected, ColumnType.isCompatible client server)
