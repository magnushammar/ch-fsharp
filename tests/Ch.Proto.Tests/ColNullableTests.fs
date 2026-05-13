module Ch.Proto.Tests.ColNullableTests

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

/// Port of `proto/col_nullable_test.go: TestColNullable_EncodeColumn`.
[<Fact>]
let ``ColNullable<string> 10-row sample matches col_nullable_str golden`` () =
    let col = ColNullable<string>(ColStr())
    let values = [|
        ValueSome "value1"
        ValueSome "value2"
        ValueNone
        ValueSome "value3"
        ValueNone
        ValueSome ""
        ValueSome ""
        ValueSome "value4"
        ValueNone
        ValueSome "value54"
    |]
    for v in values do col.Append(v)

    Assert.Equal(10, col.Rows)
    Assert.Equal("Nullable(String)", col.Type)

    let buf = Buf()
    col.EncodeColumn(buf)
    let expected = File.ReadAllBytes(goldenPath "col_nullable_str")
    Assert.Equal<byte array>(expected, buf.WrittenSpan.ToArray())

    let dec = ColNullable<string>(ColStr())
    let ms = new MemoryStream(expected)
    dec.DecodeColumn(Reader(ms), 10)
    Assert.Equal(10, dec.Rows)
    for i in 0 .. 9 do
        Assert.Equal(values.[i], dec.Row(i))

[<Fact>]
let ``ColNullable IsNull flag matches null rows`` () =
    let col = ColNullable<string>(ColStr())
    col.Append(ValueSome "a")
    col.Append(ValueNone)
    col.Append(ValueSome "b")
    col.Append(ValueNone)
    Assert.False(col.IsNull(0))
    Assert.True (col.IsNull(1))
    Assert.False(col.IsNull(2))
    Assert.True (col.IsNull(3))

[<Fact>]
let ``ColNullable<int32> round-trips with numeric inner`` () =
    let col = ColNullable<int32>(ColInt32())
    let values = [|
        ValueSome 1; ValueNone; ValueSome 3; ValueSome 0; ValueNone; ValueSome -42
    |]
    for v in values do col.Append(v)

    let buf = Buf()
    col.EncodeColumn(buf)
    let ms = new MemoryStream(buf.WrittenSpan.ToArray())
    let dec = ColNullable<int32>(ColInt32())
    dec.DecodeColumn(Reader(ms), values.Length)

    Assert.Equal("Nullable(Int32)", dec.Type)
    for i in 0 .. values.Length - 1 do
        Assert.Equal(values.[i], dec.Row(i))

[<Fact>]
let ``ColNullable Reset clears both nulls and inner`` () =
    let col = ColNullable<string>(ColStr())
    col.Append(ValueSome "x")
    col.Append(ValueNone)
    Assert.Equal(2, col.Rows)
    col.Reset()
    Assert.Equal(0, col.Rows)
    Assert.Equal(0, col.Nulls.Rows)
    Assert.Equal(0, col.Inner.Rows)
