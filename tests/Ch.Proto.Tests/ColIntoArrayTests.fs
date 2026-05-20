module Ch.Proto.Tests.ColIntoArrayTests

open System
open System.Buffers.Binary
open System.IO
open Expecto
open Ch.Proto

/// Build a little-endian byte payload of `n` sequential UInt64 starting
/// at `start`. Same idiom as `ColUInt64Tests`.
let private payloadU64 (start: uint64) (n: int) : byte array =
    let bytes : byte array = Array.zeroCreate (n * 8)
    for i in 0 .. n - 1 do
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(i * 8, 8), start + uint64 i)
    bytes

let private payloadF64 (start: float) (n: int) : byte array =
    let bytes : byte array = Array.zeroCreate (n * 8)
    for i in 0 .. n - 1 do
        BinaryPrimitives.WriteDoubleLittleEndian(
            bytes.AsSpan(i * 8, 8), start + float i)
    bytes

[<Tests>]
let tests = testList "ColIntoArray" [
    testCase "single-block decode fills destination from offset 0" <| fun _ ->
        let n = 1000
        let dst : int64 array = Array.zeroCreate n
        let col = ColIntoArray<int64>(dst)
        let payload : byte array = Array.zeroCreate (n * 8)
        for i in 0 .. n - 1 do
            BinaryPrimitives.WriteInt64LittleEndian(
                payload.AsSpan(i * 8, 8), int64 i)
        let ms = new MemoryStream(payload)
        let r = Reader(ms)

        col.DecodeColumn(r, n)

        Expect.equal col.Rows n "Rows reflects offset"
        Expect.equal col.Type "Int64" "Type string"
        Expect.equal (int ms.Position) (n * 8) "consumed bytes"
        for i in 0 .. n - 1 do
            Expect.equal dst.[i] (int64 i) "dst row"

    testCase "multi-block decode appends blocks consecutively" <| fun _ ->
        let n1 = 100
        let n2 = 150
        let n3 = 50
        let total = n1 + n2 + n3
        let dst : uint64 array = Array.zeroCreate total
        let col = ColIntoArray<uint64>(dst)

        col.DecodeColumn(Reader(new MemoryStream(payloadU64 0UL    n1)), n1)
        Expect.equal col.Rows n1 "Rows after block 1"
        col.DecodeColumn(Reader(new MemoryStream(payloadU64 100UL  n2)), n2)
        Expect.equal col.Rows (n1 + n2) "Rows after block 2"
        col.DecodeColumn(Reader(new MemoryStream(payloadU64 250UL  n3)), n3)
        Expect.equal col.Rows total "Rows after block 3"

        for i in 0 .. total - 1 do
            Expect.equal dst.[i] (uint64 i) "row i is sequential across blocks"

    testCase "AsSpan returns exactly Rows items" <| fun _ ->
        let dst : float array = Array.zeroCreate 1000
        let col = ColIntoArray<float>(dst)
        col.DecodeColumn(Reader(new MemoryStream(payloadF64 1.5 100)), 100)

        let span = col.AsSpan()
        Expect.equal span.Length 100 "span length matches Rows"
        Expect.equal span.[0] 1.5 "first row"
        Expect.equal span.[99] (1.5 + 99.0) "last row"

    testCase "Array property exposes the caller-owned destination" <| fun _ ->
        let dst : int64 array = Array.zeroCreate 32
        let col = ColIntoArray<int64>(dst)
        Expect.isTrue (obj.ReferenceEquals(col.Array, dst)) "Array IS dst"

    testCase "AsSpan stable across DecodeColumn (array does not move)" <| fun _ ->
        // Contrast with ColPrimitive whose AsSpan invalidates on resize.
        // ColIntoArray's dst is caller-owned and never resizes; a span
        // taken on the dst array points at the same memory across
        // arbitrary decodes (the Length you see won't grow, but it
        // remains valid; re-acquire to see new rows).
        let dst : int64 array = Array.zeroCreate 200
        let col = ColIntoArray<int64>(dst)
        let payload1 : byte array = Array.zeroCreate (100 * 8)
        for i in 0 .. 99 do
            BinaryPrimitives.WriteInt64LittleEndian(
                payload1.AsSpan(i * 8, 8), int64 i)
        col.DecodeColumn(Reader(new MemoryStream(payload1)), 100)

        let span1 = ReadOnlySpan(dst, 0, 100)
        Expect.equal span1.[0] 0L "span1 row 0 before second decode"
        Expect.equal span1.[99] 99L "span1 row 99 before second decode"

        // Decode another 50 rows — does NOT invalidate span1's view of
        // the first 100 rows (the underlying array is the same).
        let payload2 : byte array = Array.zeroCreate (50 * 8)
        for i in 0 .. 49 do
            BinaryPrimitives.WriteInt64LittleEndian(
                payload2.AsSpan(i * 8, 8), int64 (1000 + i))
        col.DecodeColumn(Reader(new MemoryStream(payload2)), 50)

        Expect.equal col.Rows 150 "Rows extended"
        // span1 still sees the first 100 rows unchanged.
        Expect.equal span1.[0] 0L "span1 row 0 unchanged"
        Expect.equal span1.[99] 99L "span1 row 99 unchanged"
        // New span sees the extended range.
        let span2 = col.AsSpan()
        Expect.equal span2.Length 150 "fresh span sees all 150 rows"
        Expect.equal span2.[100] 1000L "second-block row visible via fresh span"

    testCase "DecodeColumn throws on overflow" <| fun _ ->
        let dst : int64 array = Array.zeroCreate 10
        let col = ColIntoArray<int64>(dst)
        let payload : byte array = Array.zeroCreate (20 * 8)
        let r = Reader(new MemoryStream(payload))
        Expect.throwsT<InvalidOperationException>
            (fun () -> col.DecodeColumn(r, 20))
            "20 rows into a 10-slot array must throw"
        Expect.equal col.Rows 0 "Rows unchanged after failed decode"

    testCase "DecodeColumn throws when block straddles the array end" <| fun _ ->
        let dst : int64 array = Array.zeroCreate 100
        let col = ColIntoArray<int64>(dst)
        col.DecodeColumn(Reader(new MemoryStream(Array.zeroCreate (60 * 8))), 60)
        let payload : byte array = Array.zeroCreate (50 * 8)
        let r = Reader(new MemoryStream(payload))
        Expect.throwsT<InvalidOperationException>
            (fun () -> col.DecodeColumn(r, 50))
            "60 already + 50 more > 100 must throw"

    testCase "Reset rewinds offset; subsequent decode overwrites" <| fun _ ->
        let dst : uint64 array = Array.zeroCreate 100
        let col = ColIntoArray<uint64>(dst)
        col.DecodeColumn(Reader(new MemoryStream(payloadU64 10UL 50)), 50)
        Expect.equal col.Rows 50 "Rows before Reset"
        Expect.equal dst.[0] 10UL "row 0 before Reset"

        col.Reset()
        Expect.equal col.Rows 0 "Rows is 0 after Reset"
        // dst still holds the old data — Reset doesn't zero, only rewinds.
        Expect.equal dst.[0] 10UL "dst not zeroed by Reset"

        col.DecodeColumn(Reader(new MemoryStream(payloadU64 999UL 30)), 30)
        Expect.equal col.Rows 30 "Rows after second decode"
        Expect.equal dst.[0] 999UL "row 0 overwritten by second decode"
        Expect.equal dst.[29] (999UL + 29UL) "row 29 from second decode"

    testCase "EncodeColumn throws (SELECT-only column)" <| fun _ ->
        let dst : int64 array = Array.zeroCreate 10
        let col = ColIntoArray<int64>(dst)
        let buf = Buf(16)
        Expect.throwsT<NotSupportedException>
            (fun () -> col.EncodeColumn(buf))
            "EncodeColumn must throw — ColIntoArray is SELECT-only"

    testCase "Implements IColumnResult correctly" <| fun _ ->
        let dst : float array = Array.zeroCreate 8
        let col = ColIntoArray<float>(dst)
        let icr = col :> IColumnResult
        Expect.equal icr.Type "Float64" "IColumnResult.Type"
        Expect.equal icr.Rows 0 "IColumnResult.Rows starts at 0"
        icr.DecodeColumn(Reader(new MemoryStream(payloadF64 1.5 5)), 5)
        Expect.equal icr.Rows 5 "IColumnResult.Rows after decode"
        icr.Reset()
        Expect.equal icr.Rows 0 "IColumnResult.Reset rewinds"

    testCase "decode of zero rows is a no-op" <| fun _ ->
        let dst : int64 array = Array.zeroCreate 100
        let col = ColIntoArray<int64>(dst)
        col.DecodeColumn(Reader(new MemoryStream(Array.empty<byte>)), 0)
        Expect.equal col.Rows 0 "Rows still 0 after empty decode"

    testCase "Type strings cover the supported primitive set" <| fun _ ->
        Expect.equal (ColIntoArray<int8>([||]).Type)    "Int8"    "Int8"
        Expect.equal (ColIntoArray<int16>([||]).Type)   "Int16"   "Int16"
        Expect.equal (ColIntoArray<int32>([||]).Type)   "Int32"   "Int32"
        Expect.equal (ColIntoArray<int64>([||]).Type)   "Int64"   "Int64"
        Expect.equal (ColIntoArray<uint8>([||]).Type)   "UInt8"   "UInt8"
        Expect.equal (ColIntoArray<uint16>([||]).Type)  "UInt16"  "UInt16"
        Expect.equal (ColIntoArray<uint32>([||]).Type)  "UInt32"  "UInt32"
        Expect.equal (ColIntoArray<uint64>([||]).Type)  "UInt64"  "UInt64"
        Expect.equal (ColIntoArray<float32>([||]).Type) "Float32" "Float32"
        Expect.equal (ColIntoArray<float>([||]).Type)   "Float64" "Float64"
        Expect.equal (ColIntoArray<bool>([||]).Type)    "Bool"    "Bool"

    testCase "ElemSize matches sizeof<'T>" <| fun _ ->
        Expect.equal (ColIntoArray<int8>([||]).ElemSize)    1 "Int8"
        Expect.equal (ColIntoArray<int16>([||]).ElemSize)   2 "Int16"
        Expect.equal (ColIntoArray<int32>([||]).ElemSize)   4 "Int32"
        Expect.equal (ColIntoArray<int64>([||]).ElemSize)   8 "Int64"
        Expect.equal (ColIntoArray<float32>([||]).ElemSize) 4 "Float32"
        Expect.equal (ColIntoArray<float>([||]).ElemSize)   8 "Float64"
        Expect.equal (ColIntoArray<bool>([||]).ElemSize)    1 "Bool"

    testCase "Bool decodes 1 byte per row through MemoryMarshal" <| fun _ ->
        // ClickHouse Bool wire format is 1 byte per row, 0 / 1. The
        // reinterpret cast on a bool span is the one non-obvious
        // element type — exercise an actual decode, not just .Type.
        let dst : bool array = Array.zeroCreate 4
        let col = ColIntoArray<bool>(dst)
        col.DecodeColumn(Reader(new MemoryStream([| 1uy; 0uy; 1uy; 1uy |])), 4)
        Expect.equal col.Rows 4 "all four bool rows decoded"
        Expect.sequenceEqual dst [ true; false; true; true ] "bytes mapped to bools"

    testCase "DecodeColumn on a truncated stream throws, Rows unchanged" <| fun _ ->
        // The stream carries only 10 rows' worth of bytes but the block
        // claims 50. ReadFull throws part-way; offset must NOT advance,
        // so Rows stays put (contrast the overflow case, which throws
        // before any read).
        let dst : int64 array = Array.zeroCreate 100
        let col = ColIntoArray<int64>(dst)
        let truncated : byte array = Array.zeroCreate (10 * 8)
        let r = Reader(new MemoryStream(truncated))
        Expect.throwsT<UnexpectedEndOfStreamException>
            (fun () -> col.DecodeColumn(r, 50))
            "decoding 50 rows from a 10-row stream must throw"
        Expect.equal col.Rows 0 "Rows unchanged after a failed (short-read) decode"
]
