module Ch.Proto.Tests.ColInt256Tests

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

[<Fact>]
let ``ColInt256 50-row sample matches col_int256 golden`` () =
    let col = ColInt256()
    for i in 0 .. 49 do col.Append(Int256(uint64 i))

    Assert.Equal(50, col.Rows)
    Assert.Equal("Int256", col.Type)

    let buf = Buf()
    col.EncodeColumn(buf)
    let expected = File.ReadAllBytes(goldenPath "col_int256")
    Assert.Equal<byte array>(expected, buf.WrittenSpan.ToArray())

    let dec = ColInt256()
    let ms = new MemoryStream(expected)
    dec.DecodeColumn(Reader(ms), 50)
    for i in 0 .. 49 do
        Assert.Equal(Int256(uint64 i), dec.Row(i))

[<Fact>]
let ``ColUInt256 50-row sample matches col_uint256 golden`` () =
    let col = ColUInt256()
    for i in 0 .. 49 do col.Append(UInt256(uint64 i))

    Assert.Equal(50, col.Rows)
    Assert.Equal("UInt256", col.Type)

    let buf = Buf()
    col.EncodeColumn(buf)
    let expected = File.ReadAllBytes(goldenPath "col_uint256")
    Assert.Equal<byte array>(expected, buf.WrittenSpan.ToArray())

    let dec = ColUInt256()
    let ms = new MemoryStream(expected)
    dec.DecodeColumn(Reader(ms), 50)
    for i in 0 .. 49 do
        Assert.Equal(UInt256(uint64 i), dec.Row(i))

[<Fact>]
let ``ColDecimal256 50-row sample matches col_decimal256 golden`` () =
    let col = ColDecimal256()
    for i in 0 .. 49 do col.Append(Int256(uint64 i))

    Assert.Equal(50, col.Rows)
    Assert.Equal("Decimal256", col.Type)

    let buf = Buf()
    col.EncodeColumn(buf)
    let expected = File.ReadAllBytes(goldenPath "col_decimal256")
    Assert.Equal<byte array>(expected, buf.WrittenSpan.ToArray())

[<Fact>]
let ``Int256 high half packs above the low half`` () =
    // Verify byte layout: low half occupies bytes [0..16), high half [16..32).
    let v = Int256(UInt128(0UL, 0x0102030405060708UL), UInt128(0UL, 0x1112131415161718UL))
    let col = ColInt256()
    col.Append(v)
    let buf = Buf()
    col.EncodeColumn(buf)
    let bytes = buf.WrittenSpan.ToArray()
    Assert.Equal(32, bytes.Length)
    // Low.Low = 0x0102030405060708 — bytes 0..8 in LE.
    Assert.Equal(0x08uy, bytes.[0])
    Assert.Equal(0x07uy, bytes.[1])
    Assert.Equal(0x01uy, bytes.[7])
    // High.Low = 0x1112131415161718 — bytes 16..24 in LE.
    Assert.Equal(0x18uy, bytes.[16])
    Assert.Equal(0x11uy, bytes.[23])
