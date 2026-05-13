module Ch.Proto.Tests.ColPointTests

open System
open System.IO
open Xunit
open Ch.Proto

[<Fact>]
let ``ColPoint encodes as parallel X/Y Float64 columns`` () =
    let col = ColPoint()
    col.Append({ X = 1.0; Y = 2.0 })
    col.Append({ X = -3.5; Y = 4.5 })
    col.Append({ X = 0.0; Y = 0.0 })

    Assert.Equal(3, col.Rows)
    Assert.Equal("Point", col.Type)
    Assert.Equal(3, col.X.Rows)
    Assert.Equal(3, col.Y.Rows)

    let buf = Buf()
    col.EncodeColumn(buf)
    // Wire is X column then Y column, 24+24 = 48 bytes.
    Assert.Equal(48, buf.WrittenSpan.Length)

    let dec = ColPoint()
    let ms = new MemoryStream(buf.WrittenSpan.ToArray())
    dec.DecodeColumn(Reader(ms), 3)

    Assert.Equal({ X = 1.0; Y = 2.0 }, dec.Row(0))
    Assert.Equal({ X = -3.5; Y = 4.5 }, dec.Row(1))
    Assert.Equal({ X = 0.0; Y = 0.0 }, dec.Row(2))

[<Fact>]
let ``ColPoint Append(x, y) overload matches Append(Point)`` () =
    let a = ColPoint()
    let b = ColPoint()
    a.Append({ X = 1.5; Y = -2.5 })
    b.Append(1.5, -2.5)

    let aBuf = Buf()
    let bBuf = Buf()
    a.EncodeColumn(aBuf)
    b.EncodeColumn(bBuf)
    Assert.Equal<byte array>(aBuf.WrittenSpan.ToArray(), bBuf.WrittenSpan.ToArray())

[<Fact>]
let ``ColPoint Reset clears both axes`` () =
    let col = ColPoint()
    col.Append({ X = 1.0; Y = 2.0 })
    col.Append({ X = 3.0; Y = 4.0 })
    Assert.Equal(2, col.Rows)

    col.Reset()
    Assert.Equal(0, col.Rows)
    Assert.Equal(0, col.X.Rows)
    Assert.Equal(0, col.Y.Rows)

[<Fact>]
let ``Array(Point) recursively composes`` () =
    let col = ColArr<Point>(ColPoint())
    col.Append([| { X = 1.0; Y = 2.0 }; { X = 3.0; Y = 4.0 } |])
    col.Append([| { X = 5.0; Y = 6.0 } |])
    col.Append([||])

    Assert.Equal(3, col.Rows)
    Assert.Equal("Array(Point)", col.Type)

    let buf = Buf()
    col.EncodeColumn(buf)
    let dec = ColArr<Point>(ColPoint())
    let ms = new MemoryStream(buf.WrittenSpan.ToArray())
    dec.DecodeColumn(Reader(ms), 3)

    Assert.Equal<Point array>(
        [| { X = 1.0; Y = 2.0 }; { X = 3.0; Y = 4.0 } |],
        dec.Row(0))
    Assert.Equal<Point array>([| { X = 5.0; Y = 6.0 } |], dec.Row(1))
    Assert.Equal<Point array>([||], dec.Row(2))
