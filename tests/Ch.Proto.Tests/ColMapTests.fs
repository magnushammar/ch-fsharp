module Ch.Proto.Tests.ColMapTests

open System
open System.Collections.Generic
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
let tests = testList "ColMap" [
    testCase "<string, string> golden bytes match col_map_of_str_str" <| fun _ ->
        let col = ColMap<string, string>(ColStr(), ColStr())
        let m1 = Dictionary<string, string>()
        m1.["foo"] <- "bar"
        let m2 = Dictionary<string, string>()
        m2.["like"] <- "100"
        col.Append(m1)
        col.Append(m2)

        Expect.equal col.Rows 2 "rows"
        Expect.equal col.Type "Map(String, String)" "type"

        let buf = Buf()
        col.EncodeColumn(buf)
        let expected = File.ReadAllBytes(goldenPath "col_map_of_str_str")
        Expect.equal (buf.WrittenSpan.ToArray()) expected "encoded bytes"

    testCase "<string, string> AppendKV preserves order and round-trips" <| fun _ ->
        let col = ColMap<string, string>(ColStr(), ColStr())
        col.AppendKV([| KeyValuePair("foo", "bar"); KeyValuePair("baz", "hello") |])
        col.AppendKV([| KeyValuePair("like", "100"); KeyValuePair("dislike", "200") |])

        Expect.equal col.Rows 2 "rows"
        Expect.equal (col.RowLen(0)) 2 "row 0 len"
        Expect.equal (col.RowLen(1)) 2 "row 1 len"

        let buf = Buf()
        col.EncodeColumn(buf)

        let dec = ColMap<string, string>(ColStr(), ColStr())
        let ms = new MemoryStream(buf.WrittenSpan.ToArray())
        dec.DecodeColumn(Reader(ms), 2)

        let kv0 = dec.RowKV(0)
        Expect.equal kv0.Length 2 "kv0 length"
        Expect.equal kv0.[0] (KeyValuePair("foo", "bar")) "kv0[0]"
        Expect.equal kv0.[1] (KeyValuePair("baz", "hello")) "kv0[1]"

        let kv1 = dec.RowKV(1)
        Expect.equal kv1.[0] (KeyValuePair("like", "100")) "kv1[0]"
        Expect.equal kv1.[1] (KeyValuePair("dislike", "200")) "kv1[1]"

        let row0 = dec.Row(0)
        Expect.equal row0.["foo"] "bar" "row 0 foo"
        Expect.equal row0.["baz"] "hello" "row 0 baz"

    testCase "<string, int32> typed values round-trip" <| fun _ ->
        let col = ColMap<string, int32>(ColStr(), ColInt32())
        col.AppendKV([| KeyValuePair("a", 1); KeyValuePair("b", 2); KeyValuePair("c", 3) |])
        col.AppendKV([| KeyValuePair("x", 10) |])
        col.AppendKV([||])

        Expect.equal col.Rows 3 "rows"
        Expect.equal col.Type "Map(String, Int32)" "type"

        let buf = Buf()
        col.EncodeColumn(buf)
        let dec = ColMap<string, int32>(ColStr(), ColInt32())
        let ms = new MemoryStream(buf.WrittenSpan.ToArray())
        dec.DecodeColumn(Reader(ms), 3)

        Expect.equal (dec.RowLen(0)) 3 "row 0 len"
        Expect.equal (dec.RowLen(1)) 1 "row 1 len"
        Expect.equal (dec.RowLen(2)) 0 "row 2 len"
        Expect.equal (dec.Row(0).["a"]) 1 "row 0 a"
        Expect.equal (dec.Row(1).["x"]) 10 "row 1 x"
        Expect.equal (dec.Row(2).Count) 0 "row 2 count"

    testCase "empty round-trip skips body" <| fun _ ->
        let dec = ColMap<string, string>(ColStr(), ColStr())
        let ms = new MemoryStream([||])
        dec.DecodeColumn(Reader(ms), 0)
        Expect.equal dec.Rows 0 "rows"

    testCase "Reset clears offsets and inners" <| fun _ ->
        let col = ColMap<string, string>(ColStr(), ColStr())
        col.AppendKV([| KeyValuePair("foo", "bar"); KeyValuePair("baz", "hello") |])
        Expect.equal col.Rows 1 "rows pre-reset"

        col.Reset()
        Expect.equal col.Rows 0 "rows"
        Expect.equal col.Offsets.Rows 0 "offsets"
        Expect.equal col.Keys.Rows 0 "keys"
        Expect.equal col.Values.Rows 0 "values"

    testCase "<string, int32 array> recursive Map(String, Array(Int32))" <| fun _ ->
        let col = ColMap<string, int32 array>(ColStr(), ColArr<int32>(ColInt32()))
        col.AppendKV([| KeyValuePair("evens", [| 2; 4; 6 |]); KeyValuePair("odds", [| 1; 3 |]) |])
        col.AppendKV([| KeyValuePair("empty", [||]) |])

        Expect.equal col.Rows 2 "rows"
        Expect.equal col.Type "Map(String, Array(Int32))" "type"

        let buf = Buf()
        col.EncodeColumn(buf)
        let dec = ColMap<string, int32 array>(ColStr(), ColArr<int32>(ColInt32()))
        let ms = new MemoryStream(buf.WrittenSpan.ToArray())
        dec.DecodeColumn(Reader(ms), 2)

        let kv0 = dec.RowKV(0)
        Expect.equal kv0.[0].Value [| 2; 4; 6 |] "kv0[0]"
        Expect.equal kv0.[1].Value [| 1; 3 |] "kv0[1]"
        Expect.equal (dec.RowKV(1).[0].Value) [||] "kv1[0]"
]
