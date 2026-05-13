module Ch.Proto.Tests.ColIntervalTests

open System
open System.IO
open Xunit
open Ch.Proto

[<Theory>]
[<InlineData("IntervalSecond")>]
[<InlineData("IntervalMinute")>]
[<InlineData("IntervalHour")>]
[<InlineData("IntervalDay")>]
[<InlineData("IntervalWeek")>]
[<InlineData("IntervalMonth")>]
[<InlineData("IntervalQuarter")>]
[<InlineData("IntervalYear")>]
let ``ColInterval Infer accepts every documented scale`` (typeStr: string) =
    let col = ColInterval()
    col.Infer(typeStr)
    Assert.Equal(typeStr, col.Type)

[<Fact>]
let ``ColInterval round-trips Int64 wire`` () =
    let col = ColInterval(Day)
    col.Append({ Scale = Day; Value = 5L })
    col.Append({ Scale = Day; Value = -3L })
    col.Append({ Scale = Day; Value = 0L })

    Assert.Equal(3, col.Rows)
    Assert.Equal("IntervalDay", col.Type)

    let buf = Buf()
    col.EncodeColumn(buf)
    Assert.Equal(24, buf.WrittenSpan.Length)  // 3 × Int64

    let dec = ColInterval(Day)
    let ms = new MemoryStream(buf.WrittenSpan.ToArray())
    dec.DecodeColumn(Reader(ms), 3)

    Assert.Equal({ Scale = Day; Value = 5L }, dec.Row(0))
    Assert.Equal({ Scale = Day; Value = -3L }, dec.Row(1))
    Assert.Equal({ Scale = Day; Value = 0L }, dec.Row(2))

[<Fact>]
let ``ColInterval Append rejects mismatched scale`` () =
    let col = ColInterval(Hour)
    Assert.Throws<ArgumentException>(
        fun () -> col.Append({ Scale = Day; Value = 1L })
    ) |> ignore

[<Fact>]
let ``ColInterval Reset clears values, preserves scale`` () =
    let col = ColInterval(Minute)
    col.Append({ Scale = Minute; Value = 30L })
    col.Append({ Scale = Minute; Value = 60L })
    Assert.Equal(2, col.Rows)

    col.Reset()
    Assert.Equal(0, col.Rows)
    Assert.Equal(Minute, col.Scale)
    Assert.Equal("IntervalMinute", col.Type)

[<Fact>]
let ``ColInterval Infer with bad type throws`` () =
    let col = ColInterval()
    Assert.Throws<FormatException>(
        fun () -> col.Infer("NotAnInterval")
    ) |> ignore

[<Fact>]
let ``IntervalScale string conversion is bijective`` () =
    for s in [Second; Minute; Hour; Day; Week; Month; Quarter; Year] do
        let str = IntervalScale.toTypeString s
        Assert.Equal(s, IntervalScale.fromTypeString str)
