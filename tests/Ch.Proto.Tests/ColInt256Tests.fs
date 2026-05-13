module Ch.Proto.Tests.ColInt256Tests

open System
open System.IO
open Expecto
open Ch.Proto

let private goldenPath (name: string) : string =
    let p =
        Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                "reference", "ch-go", "proto", "_golden",
                $"{name}.raw"))
    if not (File.Exists p) then failwithf "golden fixture not found: %s" p
    p

[<Tests>]
let tests = testList "ColInt256" [
    testCase "ColInt256 50-row sample matches col_int256 golden" <| fun _ ->
        let col = ColInt256()
        for i in 0 .. 49 do col.Append(Int256(uint64 i))

        Expect.equal col.Rows 50 "rows"
        Expect.equal col.Type "Int256" "type"

        let buf = Buf()
        col.EncodeColumn(buf)
        let expected = File.ReadAllBytes(goldenPath "col_int256")
        Expect.equal (buf.WrittenSpan.ToArray()) expected "encoded bytes"

        let dec = ColInt256()
        let ms = new MemoryStream(expected)
        dec.DecodeColumn(Reader(ms), 50)
        for i in 0 .. 49 do
            Expect.equal (dec.Row(i)) (Int256(uint64 i)) "row"

    testCase "ColUInt256 50-row sample matches col_uint256 golden" <| fun _ ->
        let col = ColUInt256()
        for i in 0 .. 49 do col.Append(UInt256(uint64 i))

        Expect.equal col.Rows 50 "rows"
        Expect.equal col.Type "UInt256" "type"

        let buf = Buf()
        col.EncodeColumn(buf)
        let expected = File.ReadAllBytes(goldenPath "col_uint256")
        Expect.equal (buf.WrittenSpan.ToArray()) expected "encoded bytes"

        let dec = ColUInt256()
        let ms = new MemoryStream(expected)
        dec.DecodeColumn(Reader(ms), 50)
        for i in 0 .. 49 do
            Expect.equal (dec.Row(i)) (UInt256(uint64 i)) "row"

    testCase "ColDecimal256 50-row sample matches col_decimal256 golden" <| fun _ ->
        let col = ColDecimal256()
        for i in 0 .. 49 do col.Append(Int256(uint64 i))

        Expect.equal col.Rows 50 "rows"
        Expect.equal col.Type "Decimal256" "type"

        let buf = Buf()
        col.EncodeColumn(buf)
        let expected = File.ReadAllBytes(goldenPath "col_decimal256")
        Expect.equal (buf.WrittenSpan.ToArray()) expected "encoded bytes"

    testCase "Int256 high half packs above the low half" <| fun _ ->
        let v = Int256(UInt128(0UL, 0x0102030405060708UL), UInt128(0UL, 0x1112131415161718UL))
        let col = ColInt256()
        col.Append(v)
        let buf = Buf()
        col.EncodeColumn(buf)
        let bytes = buf.WrittenSpan.ToArray()
        Expect.equal bytes.Length 32 "encoded length"
        Expect.equal bytes.[0] 0x08uy "byte 0"
        Expect.equal bytes.[1] 0x07uy "byte 1"
        Expect.equal bytes.[7] 0x01uy "byte 7"
        Expect.equal bytes.[16] 0x18uy "byte 16"
        Expect.equal bytes.[23] 0x11uy "byte 23"
]
