module Ch.Proto.Tests.ColStrTests

open System
open System.IO
open System.Text
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
let tests = testList "ColStr" [
    testCase "encodes 6-row sample against col_str golden" <| fun _ ->
        let input = [| "foo"; "bar"; "ClickHouse"; "one"; ""; "1" |]
        let col = ColStr()
        for s in input do col.Append(s)
        Expect.equal col.Rows input.Length "rows"
        for i in 0 .. input.Length - 1 do
            Expect.equal (col.Row(i)) input.[i] "row"

        let buf = Buf()
        col.EncodeColumn(buf)
        let expected = File.ReadAllBytes(goldenPath "col_str")
        Expect.equal (buf.WrittenSpan.ToArray()) expected "encoded bytes"

        let ms = new MemoryStream(expected)
        let r = Reader(ms)
        let dec = ColStr()
        dec.DecodeColumn(r, input.Length)
        Expect.equal dec.Rows input.Length "decoded rows"
        for i in 0 .. input.Length - 1 do
            Expect.equal (dec.Row(i)) input.[i] "decoded row"

    testCase "AppendBytes round-trips against col_str_bytes golden" <| fun _ ->
        let col = ColStr()
        col.AppendBytes(ReadOnlySpan(Encoding.UTF8.GetBytes("Hello, World!")))
        col.AppendBytes(ReadOnlySpan(Encoding.UTF8.GetBytes("ClickHouse")))
        Expect.equal col.Rows 2 "rows"

        let buf = Buf()
        col.EncodeColumn(buf)
        let expected = File.ReadAllBytes(goldenPath "col_str_bytes")
        Expect.equal (buf.WrittenSpan.ToArray()) expected "encoded bytes"

        let ms = new MemoryStream(expected)
        let r = Reader(ms)
        let dec = ColStr()
        dec.DecodeColumn(r, 2)
        Expect.equal (dec.Row(0)) "Hello, World!" "row 0"
        Expect.equal (dec.Row(1)) "ClickHouse" "row 1"

    testCase "handles empty strings" <| fun _ ->
        let col = ColStr()
        col.Append(""); col.Append(""); col.Append("")
        Expect.equal col.Rows 3 "rows"
        Expect.equal (col.Row(0)) "" "row 0"
        Expect.equal (col.Row(1)) "" "row 1"
        Expect.equal (col.Row(2)) "" "row 2"

        let buf = Buf()
        col.EncodeColumn(buf)
        Expect.equal (buf.WrittenSpan.ToArray()) [| 0uy; 0uy; 0uy |] "encoded bytes"

    testCase "handles UTF-8 multi-byte characters" <| fun _ ->
        let col = ColStr()
        col.Append("café")
        col.Append("日本語")
        col.Append("🚀ClickHouse")
        Expect.equal col.Rows 3 "rows"

        let buf = Buf()
        col.EncodeColumn(buf)

        let ms = new MemoryStream(buf.WrittenSpan.ToArray())
        let r = Reader(ms)
        let dec = ColStr()
        dec.DecodeColumn(r, 3)
        Expect.equal (dec.Row(0)) "café" "row 0"
        Expect.equal (dec.Row(1)) "日本語" "row 1"
        Expect.equal (dec.Row(2)) "🚀ClickHouse" "row 2"

    testCase "Reset preserves capacity" <| fun _ ->
        let col = ColStr()
        col.Append("hello"); col.Append("world")
        let cap = col.RawData.Length

        col.Reset()
        Expect.equal col.Rows 0 "rows after reset"
        Expect.equal col.DataLength 0 "data length after reset"
        Expect.equal col.RawData.Length cap "capacity preserved"

        col.Append("again")
        Expect.equal col.Rows 1 "rows after re-append"
        Expect.equal (col.Row(0)) "again" "row 0"
]
