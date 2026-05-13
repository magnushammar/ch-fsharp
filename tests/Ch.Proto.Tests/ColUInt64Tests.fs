module Ch.Proto.Tests.ColUInt64Tests

open System
open System.Buffers.Binary
open System.IO
open Expecto
open Ch.Proto

[<Tests>]
let tests = testList "ColUInt64" [
    testCase "decodes 1000 sequential UInt64 from synthetic bytes" <| fun _ ->
        let rows = 1000
        let payload : byte array = Array.zeroCreate (rows * 8)
        for i in 0 .. rows - 1 do
            BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(i * 8, 8), uint64 i)

        let ms = new MemoryStream(payload)
        let r = Reader(ms)
        let col = ColUInt64()
        col.DecodeColumn(r, rows)

        Expect.equal col.Rows rows "rows"
        Expect.equal (int ms.Position) (rows * 8) "consumed bytes"

        let span = col.AsSpan()
        Expect.equal span.Length rows "span length"
        for i in 0 .. rows - 1 do
            Expect.equal span.[i] (uint64 i) "span row"
            Expect.equal (col.Row(i)) (uint64 i) "Row(i)"

    testCase "DecodeColumn reuses buffer when rows fits" <| fun _ ->
        let col = ColUInt64()
        let payload1 : byte array = Array.zeroCreate 80
        col.DecodeColumn(Reader(new MemoryStream(payload1)), 10)
        let bufRef1 = col.RawBuffer

        let payload2 : byte array = Array.zeroCreate 80
        col.DecodeColumn(Reader(new MemoryStream(payload2)), 10)
        let bufRef2 = col.RawBuffer

        Expect.isTrue (obj.ReferenceEquals(bufRef1, bufRef2)) "buffer reused"

    testCase "DecodeColumn grows buffer when needed" <| fun _ ->
        let col = ColUInt64()
        let small : byte array = Array.zeroCreate 80
        col.DecodeColumn(Reader(new MemoryStream(small)), 10)

        let bigPayload : byte array = Array.zeroCreate 8000
        col.DecodeColumn(Reader(new MemoryStream(bigPayload)), 1000)

        Expect.equal col.Rows 1000 "rows"
        Expect.isTrue (col.RawBuffer.Length >= 8000) "buffer grown"
]
