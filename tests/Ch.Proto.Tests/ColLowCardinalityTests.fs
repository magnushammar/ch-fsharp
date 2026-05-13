module Ch.Proto.Tests.ColLowCardinalityTests

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
let tests = testList "ColLowCardinality" [
    testCase "Rows is eager: reflects Appends before Prepare" <| fun _ ->
        let lc = ColLowCardinality<string>(ColStr())
        Expect.equal lc.Rows 0 "initial"

        lc.Append("alpha"); lc.Append("beta"); lc.Append("alpha")
        Expect.equal lc.Rows 3 "after appends (pre-Prepare)"

        let buf = Buf()
        lc.EncodeColumn(buf)
        Expect.equal lc.Rows 3 "after encode"

        lc.Reset()
        Expect.equal lc.Rows 0 "after reset"

    testCase "<string> 25 rows over 3 unique strings (k=8) matches golden" <| fun _ ->
        let values = [| "neo"; "trinity"; "morpheus" |]
        let lc = ColLowCardinality<string>(ColStr())
        for i in 0 .. 24 do lc.Append(values.[i % 3])
        Expect.equal lc.Type "LowCardinality(String)" "type"

        let buf = Buf()
        lc.EncodeColumn(buf)
        let expected = File.ReadAllBytes(goldenPath "col_low_cardinality_i_str_k_8")
        Expect.equal (buf.WrittenSpan.ToArray()) expected "encoded bytes"

        let dec = ColLowCardinality<string>(ColStr())
        let ms = new MemoryStream(expected)
        dec.DecodeColumn(Reader(ms), 25)

        Expect.equal dec.Rows 25 "rows"
        Expect.equal dec.DictRows 3 "dict rows"
        Expect.equal dec.KeyWidth 1 "key width"
        for i in 0 .. 24 do
            Expect.equal (dec.Row(i)) values.[i % 3] "row"

    testCase "<string> exposes Inner and Dictionary for fast iteration" <| fun _ ->
        let lc = ColLowCardinality<string>(ColStr())
        for _ in 0 .. 9 do lc.Append("only-one")

        let buf = Buf()
        lc.EncodeColumn(buf)
        let ms = new MemoryStream(buf.WrittenSpan.ToArray())
        let dec = ColLowCardinality<string>(ColStr())
        dec.DecodeColumn(Reader(ms), 10)

        Expect.equal dec.DictRows 1 "dict rows"
        Expect.equal dec.Rows 10 "rows"
        Expect.equal dec.KeyWidth 1 "key width"
        Expect.equal dec.Dictionary.[0] "only-one" "dict entry"
        for k in dec.Keys.ToArray() do Expect.equal k 0uy "key byte"

    testCase "<string> empty column encodes to empty bytes" <| fun _ ->
        let lc = ColLowCardinality<string>(ColStr())
        let buf = Buf()
        lc.EncodeColumn(buf)
        Expect.equal buf.Length 0 "buf length"
        Expect.equal lc.Rows 0 "rows"

    testCase "<string> Reset preserves capacity but clears rows" <| fun _ ->
        let lc = ColLowCardinality<string>(ColStr())
        for s in [| "a"; "b"; "a"; "c" |] do lc.Append(s)
        let buf = Buf()
        lc.EncodeColumn(buf)
        Expect.equal lc.Rows 4 "rows pre-reset"

        lc.Reset()
        Expect.equal lc.Rows 0 "rows post-reset"
        let buf2 = Buf()
        lc.EncodeColumn(buf2)
        Expect.equal buf2.Length 0 "empty encode after reset"

    testCase "<int32> over numeric T also works" <| fun _ ->
        let lc = ColLowCardinality<int32>(ColInt32())
        for i in 0 .. 49 do lc.Append(i % 5)
        let buf = Buf()
        lc.EncodeColumn(buf)
        Expect.equal lc.Type "LowCardinality(Int32)" "type"

        let dec = ColLowCardinality<int32>(ColInt32())
        let ms = new MemoryStream(buf.WrittenSpan.ToArray())
        dec.DecodeColumn(Reader(ms), 50)

        Expect.equal dec.Rows 50 "rows"
        Expect.equal dec.DictRows 5 "dict rows"
        for i in 0 .. 49 do
            Expect.equal (dec.Row(i)) (i % 5) "row"

    testCase "DecodeState rejects bad version" <| fun _ ->
        let lc = ColLowCardinality<string>(ColStr())
        let bad : byte array = Array.zeroCreate 8
        bad.[0] <- 2uy
        let ms = new MemoryStream(bad)
        Expect.throwsT<InvalidDataException>
            (fun () -> lc.DecodeState(Reader(ms)))
            "bad version"
]
