module Ch.Proto.Tests.ColNothingTests

open System
open System.IO
open Expecto
open Ch.Proto

[<Tests>]
let tests = testList "ColNothing" [
    testCase "encodes one byte per row" <| fun _ ->
        let col = ColNothing()
        col.AppendN(5)
        Expect.equal col.Rows 5 "rows"
        Expect.equal col.Type "Nothing" "type"

        let buf = Buf()
        col.EncodeColumn(buf)
        Expect.equal buf.WrittenSpan.Length 5 "encoded length"
        for b in buf.WrittenSpan.ToArray() do
            Expect.equal b 0uy "padding byte"

    testCase "decode consumes padding bytes" <| fun _ ->
        let dec = ColNothing()
        let ms = new MemoryStream([| 1uy; 2uy; 3uy; 4uy; 5uy |])
        dec.DecodeColumn(Reader(ms), 5)
        Expect.equal dec.Rows 5 "rows"

    testCase "Reset clears count" <| fun _ ->
        let col = ColNothing()
        col.AppendN(3)
        Expect.equal col.Rows 3 "rows pre-reset"
        col.Reset()
        Expect.equal col.Rows 0 "rows"

    testCase "Nullable(Nothing) is all-NULL" <| fun _ ->
        let col = ColNullable<Nothing>(ColNothing())
        col.Append(ValueNone); col.Append(ValueNone); col.Append(ValueNone)
        Expect.equal col.Rows 3 "rows"
        Expect.equal col.Type "Nullable(Nothing)" "type"

        let buf = Buf()
        col.EncodeColumn(buf)
        let dec = ColNullable<Nothing>(ColNothing())
        let ms = new MemoryStream(buf.WrittenSpan.ToArray())
        dec.DecodeColumn(Reader(ms), 3)
        Expect.isTrue (dec.IsNull(0)) "row 0 null"
        Expect.isTrue (dec.IsNull(1)) "row 1 null"
        Expect.isTrue (dec.IsNull(2)) "row 2 null"
]
