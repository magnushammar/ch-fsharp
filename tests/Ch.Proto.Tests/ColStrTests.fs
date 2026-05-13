module Ch.Proto.Tests.ColStrTests

open System
open System.IO
open System.Text
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

/// Port of `proto/col_str_test.go: TestColStr_EncodeColumn`.
[<Fact>]
let ``ColStr encodes 6-row sample against col_str golden`` () =
    let input = [| "foo"; "bar"; "ClickHouse"; "one"; ""; "1" |]
    let col = ColStr()
    for s in input do
        col.Append(s)
    Assert.Equal(input.Length, col.Rows)
    for i in 0 .. input.Length - 1 do
        Assert.Equal(input.[i], col.Row(i))

    let buf = Buf()
    col.EncodeColumn(buf)
    let expected = File.ReadAllBytes(goldenPath "col_str")
    Assert.Equal<byte array>(expected, buf.WrittenSpan.ToArray())

    let ms = new MemoryStream(expected)
    let r = Reader(ms)
    let dec = ColStr()
    dec.DecodeColumn(r, input.Length)
    Assert.Equal(input.Length, dec.Rows)
    for i in 0 .. input.Length - 1 do
        Assert.Equal(input.[i], dec.Row(i))

/// Port of `proto/col_str_test.go: TestColStr_AppendBytes`.
[<Fact>]
let ``ColStr.AppendBytes round-trips against col_str_bytes golden`` () =
    let col = ColStr()
    col.AppendBytes(ReadOnlySpan(Encoding.UTF8.GetBytes("Hello, World!")))
    col.AppendBytes(ReadOnlySpan(Encoding.UTF8.GetBytes("ClickHouse")))
    Assert.Equal(2, col.Rows)

    let buf = Buf()
    col.EncodeColumn(buf)
    let expected = File.ReadAllBytes(goldenPath "col_str_bytes")
    Assert.Equal<byte array>(expected, buf.WrittenSpan.ToArray())

    let ms = new MemoryStream(expected)
    let r = Reader(ms)
    let dec = ColStr()
    dec.DecodeColumn(r, 2)
    Assert.Equal("Hello, World!", dec.Row(0))
    Assert.Equal("ClickHouse", dec.Row(1))

[<Fact>]
let ``ColStr handles empty strings`` () =
    let col = ColStr()
    col.Append("")
    col.Append("")
    col.Append("")
    Assert.Equal(3, col.Rows)
    Assert.Equal("", col.Row(0))
    Assert.Equal("", col.Row(1))
    Assert.Equal("", col.Row(2))

    let buf = Buf()
    col.EncodeColumn(buf)
    // 3 × uvarint(0) = 3 bytes of 0x00
    Assert.Equal<byte array>([| 0uy; 0uy; 0uy |], buf.WrittenSpan.ToArray())

[<Fact>]
let ``ColStr handles UTF-8 multi-byte characters`` () =
    let col = ColStr()
    col.Append("café")           // 5 UTF-8 bytes (é is 2)
    col.Append("日本語")          // 9 UTF-8 bytes
    col.Append("🚀ClickHouse")   // 4 + 10 = 14 UTF-8 bytes (🚀 is 4)
    Assert.Equal(3, col.Rows)

    let buf = Buf()
    col.EncodeColumn(buf)

    let ms = new MemoryStream(buf.WrittenSpan.ToArray())
    let r = Reader(ms)
    let dec = ColStr()
    dec.DecodeColumn(r, 3)
    Assert.Equal("café", dec.Row(0))
    Assert.Equal("日本語", dec.Row(1))
    Assert.Equal("🚀ClickHouse", dec.Row(2))

[<Fact>]
let ``ColStr Reset preserves capacity`` () =
    let col = ColStr()
    col.Append("hello")
    col.Append("world")
    let cap = col.RawData.Length

    col.Reset()
    Assert.Equal(0, col.Rows)
    Assert.Equal(0, col.DataLength)
    Assert.Equal(cap, col.RawData.Length)

    col.Append("again")
    Assert.Equal(1, col.Rows)
    Assert.Equal("again", col.Row(0))
