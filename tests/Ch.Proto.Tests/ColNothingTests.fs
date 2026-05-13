module Ch.Proto.Tests.ColNothingTests

open System
open System.IO
open Xunit
open Ch.Proto

[<Fact>]
let ``ColNothing encodes one byte per row`` () =
    let col = ColNothing()
    col.AppendN(5)
    Assert.Equal(5, col.Rows)
    Assert.Equal("Nothing", col.Type)

    let buf = Buf()
    col.EncodeColumn(buf)
    Assert.Equal(5, buf.WrittenSpan.Length)
    // Padding bytes are zero — server ignores content, we send zeros.
    for b in buf.WrittenSpan.ToArray() do
        Assert.Equal(0uy, b)

[<Fact>]
let ``ColNothing decode consumes padding bytes`` () =
    let dec = ColNothing()
    // 5 arbitrary bytes — server can send anything; we don't interpret.
    let ms = new MemoryStream([| 1uy; 2uy; 3uy; 4uy; 5uy |])
    dec.DecodeColumn(Reader(ms), 5)
    Assert.Equal(5, dec.Rows)

[<Fact>]
let ``ColNothing Reset clears count`` () =
    let col = ColNothing()
    col.AppendN(3)
    Assert.Equal(3, col.Rows)
    col.Reset()
    Assert.Equal(0, col.Rows)

[<Fact>]
let ``Nullable(Nothing) is all-NULL`` () =
    // `SELECT NULL` returns Nullable(Nothing) — null mask = 1 for every row,
    // inner is Nothing (padding only). The composition should round-trip.
    let col = ColNullable<Nothing>(ColNothing())
    col.Append(ValueNone)
    col.Append(ValueNone)
    col.Append(ValueNone)
    Assert.Equal(3, col.Rows)
    Assert.Equal("Nullable(Nothing)", col.Type)

    let buf = Buf()
    col.EncodeColumn(buf)
    let dec = ColNullable<Nothing>(ColNothing())
    let ms = new MemoryStream(buf.WrittenSpan.ToArray())
    dec.DecodeColumn(Reader(ms), 3)
    Assert.True(dec.IsNull(0))
    Assert.True(dec.IsNull(1))
    Assert.True(dec.IsNull(2))
