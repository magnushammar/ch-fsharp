module Ch.Proto.Tests.ColUInt64Tests

open System
open System.Buffers.Binary
open System.IO
open Xunit
open Ch.Proto

[<Fact>]
let ``decodes 1000 sequential UInt64 from synthetic bytes`` () =
    let rows = 1000
    let payload : byte array = Array.zeroCreate (rows * 8)
    for i in 0 .. rows - 1 do
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(i * 8, 8), uint64 i)

    let ms = new MemoryStream(payload)
    let r = Reader(ms)
    let col = ColUInt64()
    col.DecodeColumn(r, rows)

    Assert.Equal(rows, col.Rows)
    Assert.Equal(rows * 8, ms.Position |> int)

    let span = col.AsSpan()
    Assert.Equal(rows, span.Length)
    for i in 0 .. rows - 1 do
        Assert.Equal(uint64 i, span.[i])
        Assert.Equal(uint64 i, col.Row(i))

[<Fact>]
let ``DecodeColumn reuses buffer when rows fits`` () =
    let col = ColUInt64()
    let payload1 : byte array = Array.zeroCreate 80
    let ms1 = new MemoryStream(payload1)
    col.DecodeColumn(Reader(ms1), 10)
    let bufRef1 = col.RawBuffer

    let payload2 : byte array = Array.zeroCreate 80
    let ms2 = new MemoryStream(payload2)
    col.DecodeColumn(Reader(ms2), 10)
    let bufRef2 = col.RawBuffer

    Assert.Same(bufRef1, bufRef2)

[<Fact>]
let ``DecodeColumn grows buffer when needed`` () =
    let col = ColUInt64()
    let small : byte array = Array.zeroCreate 80
    col.DecodeColumn(Reader(new MemoryStream(small)), 10)

    let bigPayload : byte array = Array.zeroCreate 8000
    col.DecodeColumn(Reader(new MemoryStream(bigPayload)), 1000)

    Assert.Equal(1000, col.Rows)
    Assert.True(col.RawBuffer.Length >= 8000)
