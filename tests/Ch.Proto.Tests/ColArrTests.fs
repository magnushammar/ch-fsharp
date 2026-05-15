module Ch.Proto.Tests.ColArrTests

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
let tests = testList "ColArr" [
    testCase "<int8> 5-row sample matches col_arr_int8_manual golden" <| fun _ ->
        let col = ColArr<int8>(ColInt8())
        let values =
            [|
                for i in 0 .. 4 ->
                    [| for j in 0 .. i + 1 -> int8 (10 + j * 2 + 3 * i) |]
            |]
        for v in values do col.Append(v)

        Expect.equal col.Rows 5 "rows"
        Expect.equal col.Type "Array(Int8)" "type"

        let buf = Buf()
        col.EncodeColumn(buf)
        let expected = File.ReadAllBytes(goldenPath "col_arr_int8_manual")
        Expect.equal (buf.WrittenSpan.ToArray()) expected "encoded bytes"

        let dec = ColArr<int8>(ColInt8())
        let ms = new MemoryStream(expected)
        dec.DecodeColumn(Reader(ms), 5)
        Expect.equal dec.Rows 5 "decoded rows"
        for i in 0 .. 4 do
            Expect.equal (dec.Row(i)) values.[i] "row"

    testCase "<string> stores variable-length string rows" <| fun _ ->
        let col = ColArr<string>(ColStr())
        col.Append([| "alpha"; "beta" |])
        col.Append([| "gamma" |])
        col.Append([||])
        col.Append([| "delta"; "epsilon"; "zeta" |])

        Expect.equal col.Rows 4 "rows"
        Expect.equal col.Type "Array(String)" "type"

        let buf = Buf()
        col.EncodeColumn(buf)
        let ms = new MemoryStream(buf.WrittenSpan.ToArray())
        let dec = ColArr<string>(ColStr())
        dec.DecodeColumn(Reader(ms), 4)

        Expect.equal (dec.Row(0)) [| "alpha"; "beta" |] "row 0"
        Expect.equal (dec.Row(1)) [| "gamma" |] "row 1"
        Expect.equal (dec.Row(2)) [||] "row 2"
        Expect.equal (dec.Row(3)) [| "delta"; "epsilon"; "zeta" |] "row 3"

    testCase "<int32[]> recursive Array(Array(Int32))" <| fun _ ->
        let inner = ColArr<int32>(ColInt32())
        let outer = ColArr<int32 array>(inner)
        outer.Append([| [| 1; 2 |]; [| 3; 4; 5 |] |])
        outer.Append([| [| 10 |] |])
        outer.Append([||])

        Expect.equal outer.Rows 3 "rows"
        Expect.equal outer.Type "Array(Array(Int32))" "type"

        let buf = Buf()
        outer.EncodeColumn(buf)
        let ms = new MemoryStream(buf.WrittenSpan.ToArray())
        let dec = ColArr<int32 array>(ColArr<int32>(ColInt32()))
        dec.DecodeColumn(Reader(ms), 3)

        let row0 = dec.Row(0)
        Expect.equal row0.Length 2 "row 0 length"
        Expect.equal row0.[0] [| 1; 2 |] "row 0, item 0"
        Expect.equal row0.[1] [| 3; 4; 5 |] "row 0, item 1"
        Expect.equal (dec.Row(1)) [| [| 10 |] |] "row 1"
        Expect.equal (dec.Row(2)) ([||] : int32 array array) "row 2"

    testCase "Reset clears offsets and inner" <| fun _ ->
        let col = ColArr<int32>(ColInt32())
        col.Append([| 1; 2; 3 |])
        col.Append([| 4; 5 |])
        Expect.equal col.Rows 2 "rows pre-reset"

        col.Reset()
        Expect.equal col.Rows 0 "rows"
        Expect.equal col.OffsetsCount 0 "offsets"
        Expect.equal col.Inner.Rows 0 "inner"
]
