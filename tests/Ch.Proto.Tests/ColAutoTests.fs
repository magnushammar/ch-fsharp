module Ch.Proto.Tests.ColAutoTests

open System
open System.IO
open Xunit
open Ch.Proto

[<Theory>]
[<InlineData("Int32")>]
[<InlineData("Int64")>]
[<InlineData("UInt8")>]
[<InlineData("UInt256")>]
[<InlineData("Float64")>]
[<InlineData("Bool")>]
[<InlineData("String")>]
[<InlineData("JSON")>]
[<InlineData("Date")>]
[<InlineData("Date32")>]
[<InlineData("DateTime")>]
[<InlineData("UUID")>]
[<InlineData("IPv4")>]
[<InlineData("IPv6")>]
[<InlineData("Point")>]
[<InlineData("Nothing")>]
[<InlineData("BFloat16")>]
let ``ColAuto.build dispatches scalar primitives`` (typeStr: string) =
    let col = ColAuto.build typeStr
    Assert.Equal(typeStr, col.Type)

[<Theory>]
[<InlineData("Decimal(9, 2)", "Decimal32")>]
[<InlineData("Decimal(18, 4)", "Decimal64")>]
[<InlineData("Decimal(38, 8)", "Decimal128")>]
[<InlineData("Decimal(76, 0)", "Decimal256")>]
[<InlineData("Decimal32", "Decimal32")>]
[<InlineData("Decimal64", "Decimal64")>]
let ``ColAuto.build downcasts Decimal(P, S) to the right width`` (input: string, expected: string) =
    let col = ColAuto.build input
    Assert.Equal(expected, col.Type)

[<Fact>]
let ``ColAuto.build Enum8 parses the mapping`` () =
    let col = ColAuto.build "Enum8('off' = 0, 'on' = 1, 'dim' = 2)"
    let en = col :?> ColEnum8
    Assert.Equal(0y, en.NameToValue.["off"])
    Assert.Equal("dim", en.ValueToName.[2y])
    Assert.Equal("Enum8('off' = 0, 'on' = 1, 'dim' = 2)", col.Type)

[<Fact>]
let ``ColAuto.build DateTime64 parses precision and strips timezone`` () =
    let col1 = ColAuto.build "DateTime64(3)"
    let dt1 = col1 :?> ColDateTime64
    Assert.Equal(3, dt1.Precision)

    let col2 = ColAuto.build "DateTime64(9, 'UTC')"
    let dt2 = col2 :?> ColDateTime64
    Assert.Equal(9, dt2.Precision)

[<Fact>]
let ``ColAuto.build FixedString(N) parses width`` () =
    let col = ColAuto.build "FixedString(8)"
    Assert.Equal("FixedString(8)", col.Type)
    let fs = col :?> ColFixedStr
    Assert.Equal(8, fs.ElemSize)

[<Fact>]
let ``ColAuto.build Interval picks the scale`` () =
    let col = ColAuto.build "IntervalDay"
    let iv = col :?> ColInterval
    Assert.Equal(Day, iv.Scale)

[<Fact>]
let ``ColAuto.build rejects composite types`` () =
    Assert.Throws<NotSupportedException>(
        fun () -> ColAuto.build "Array(Int32)" |> ignore
    ) |> ignore
    Assert.Throws<NotSupportedException>(
        fun () -> ColAuto.build "Nullable(String)" |> ignore
    ) |> ignore

[<Fact>]
let ``ColAuto column instance round-trips via Infer + IColumnResult`` () =
    let auto = ColAuto()
    let inferable = auto :> IColumnResult
    let _ = (auto :> IInferable).Infer("Int32")

    Assert.Equal("Int32", inferable.Type)
    Assert.Equal(0, inferable.Rows)

    // Encode some rows via the inner ColInt32.
    let inner = auto.Inner.Value :?> ColInt32
    inner.Append(10)
    inner.Append(20)
    inner.Append(30)

    let buf = Buf()
    inferable.EncodeColumn(buf)
    Assert.Equal(12, buf.WrittenSpan.Length)  // 3 × Int32

    let other = ColAuto()
    (other :> IInferable).Infer("Int32")
    let ms = new MemoryStream(buf.WrittenSpan.ToArray())
    (other :> IColumnResult).DecodeColumn(Reader(ms), 3)
    let innerB = other.Inner.Value :?> ColInt32
    Assert.Equal(10, innerB.Row(0))
    Assert.Equal(20, innerB.Row(1))
    Assert.Equal(30, innerB.Row(2))

[<Fact>]
let ``ColumnType.normalize strips DateTime / DateTime64 timezones`` () =
    Assert.Equal("DateTime", ColumnType.normalize "DateTime('UTC')")
    Assert.Equal("DateTime64(3)", ColumnType.normalize "DateTime64(3, 'UTC')")
    Assert.Equal("Array(DateTime)", ColumnType.normalize "Array(DateTime('America/New_York'))")
