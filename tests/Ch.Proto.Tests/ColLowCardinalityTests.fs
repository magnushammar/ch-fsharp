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
        Expect.equal (dec.DictionarySpan().[0]) "only-one" "dict entry"
        for k in dec.KeysSpan().ToArray() do Expect.equal k 0uy "key byte"

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

    testCase "Append deduplicates eagerly into inner" <| fun _ ->
        // Post-refactor: dedup happens at Append time, not Prepare time.
        // The inner column should reflect the dict size immediately.
        let col = ColLowCardinality<string>(ColStr())
        col.Append("a")
        col.Append("b")
        col.Append("a")   // dup → reuses key
        col.Append("c")
        col.Append("b")   // dup → reuses key
        Expect.equal col.Inner.Rows 3 "inner holds {a, b, c} after Append"
        Expect.equal col.Rows 5 "5 rows total"

    testCase "Append + Prepare picks the right keyWidth" <| fun _ ->
        let col = ColLowCardinality<int32>(ColInt32())
        for i in 0 .. 9 do
            col.Append(i)
        col.Prepare()
        Expect.equal col.Inner.Rows 10 "10 unique values"
        Expect.equal col.KeyWidth 1 "≤256 → 1-byte keys"

    testCase "RowSpan returns inner UTF-8 bytes for LowCardinality(String)" <| fun _ ->
        // Roundtrip a few values, then read back via RowSpan and verify
        // byte equality without going through Row(i) (which would have
        // materialised the string dict).
        let lc = ColLowCardinality<string>(ColStr())
        lc.Append("alpha")
        lc.Append("beta")
        lc.Append("alpha")   // dup
        lc.Prepare()
        let buf = Buf()
        lc.EncodeColumn(buf)
        let dec = ColLowCardinality<string>(ColStr())
        let ms = new MemoryStream(buf.WrittenSpan.ToArray())
        dec.DecodeColumn(Reader(ms), 3)

        let bytes0 = dec.RowSpan(0)
        let bytes1 = dec.RowSpan(1)
        let bytes2 = dec.RowSpan(2)
        Expect.equal bytes0.Length 5 "alpha is 5 bytes"
        Expect.equal bytes1.Length 4 "beta is 4 bytes"
        Expect.equal bytes0.[0] (byte 'a') "alpha[0]"
        Expect.equal bytes1.[0] (byte 'b') "beta[0]"
        Expect.isTrue (bytes0.SequenceEqual(bytes2)) "dup row matches first"

    testCase "RowSpan throws when inner isn't byte-row" <| fun _ ->
        let lc = ColLowCardinality<int32>(ColInt32())
        lc.Append(42)
        lc.Prepare()
        let buf = Buf()
        lc.EncodeColumn(buf)
        let dec = ColLowCardinality<int32>(ColInt32())
        let ms = new MemoryStream(buf.WrittenSpan.ToArray())
        dec.DecodeColumn(Reader(ms), 1)
        Expect.throwsT<NotSupportedException>
            (fun () -> dec.RowSpan(0).Length |> ignore)
            "ColInt32 inner doesn't implement IRowBytes"

    testCase "ColLowCardinality<byte[]> auto-defaults to content-hash dedup" <| fun _ ->
        // No explicit comparer passed. Default array equality is
        // reference-based, so without the auto-default these two fresh
        // arrays with identical content would each take a dict slot.
        // With the auto-default, content dedup kicks in.
        let lc = ColLowCardinality<byte array>(ColFixedStr(8))
        let v = Array.replicate 8 0xAAuy
        let vDup = Array.replicate 8 0xAAuy   // fresh array, same content
        lc.Append(v)
        lc.Append(vDup)
        Expect.equal lc.Inner.Rows 1 "content dedup: only 1 unique value"
        Expect.equal lc.Rows 2 "2 rows total"

    testCase "ColLowCardinality<byte[]> explicit reference comparer still works" <| fun _ ->
        // The auto-default is just a default — explicit comparer wins.
        // Use HashIdentity.Reference to opt back into reference equality
        // (rarely useful, but the override path must work).
        let lc =
            ColLowCardinality<byte array>(
                ColFixedStr(8),
                HashIdentity.Reference)
        let v = Array.replicate 8 0xAAuy
        let vDup = Array.replicate 8 0xAAuy
        lc.Append(v)
        lc.Append(vDup)
        Expect.equal lc.Inner.Rows 2 "reference dedup: distinct arrays = distinct keys"
]
