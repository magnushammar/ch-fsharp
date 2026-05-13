module Ch.Proto.Tests.ColJSONStrTests

open System
open System.IO
open Expecto
open Ch.Proto

[<Tests>]
let tests = testList "ColJSONStr" [
    testCase "round-trips a small set of JSON documents" <| fun _ ->
        let col = ColJSONStr()
        col.Append("""{"a":1}""")
        col.Append("""{"hello":"world"}""")
        col.Append("[]")

        Expect.equal col.Rows 3 "rows"
        Expect.equal col.Type "JSON" "type"

        let buf = Buf()
        (col :> IStatefulColumn).EncodeState(buf)
        col.EncodeColumn(buf)

        let dec = ColJSONStr()
        let ms = new MemoryStream(buf.WrittenSpan.ToArray())
        let r = Reader(ms)
        (dec :> IStatefulColumn).DecodeState(r)
        dec.DecodeColumn(r, 3)

        Expect.equal (dec.Row(0)) """{"a":1}""" "row 0"
        Expect.equal (dec.Row(1)) """{"hello":"world"}""" "row 1"
        Expect.equal (dec.Row(2)) "[]" "row 2"

    testCase "DecodeState rejects unknown version" <| fun _ ->
        let dec = ColJSONStr()
        let buf = Buf()
        buf.PutUInt64(999UL)
        let ms = new MemoryStream(buf.WrittenSpan.ToArray())
        Expect.throwsT<FormatException>
            (fun () -> (dec :> IStatefulColumn).DecodeState(Reader(ms)))
            "bad version"

    testCase "Reset clears rows" <| fun _ ->
        let col = ColJSONStr()
        col.Append("""{}""")
        col.Append("""null""")
        Expect.equal col.Rows 2 "rows pre-reset"

        col.Reset()
        Expect.equal col.Rows 0 "rows"
]
