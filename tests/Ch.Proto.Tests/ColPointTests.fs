module Ch.Proto.Tests.ColPointTests

open System
open System.IO
open Expecto
open Ch.Proto

[<Tests>]
let tests = testList "ColPoint" [
    testCase "encodes as parallel X/Y Float64 columns" <| fun _ ->
        let col = ColPoint()
        col.Append({ X = 1.0; Y = 2.0 })
        col.Append({ X = -3.5; Y = 4.5 })
        col.Append({ X = 0.0; Y = 0.0 })

        Expect.equal col.Rows 3 "rows"
        Expect.equal col.Type "Point" "type"
        Expect.equal col.X.Rows 3 "x rows"
        Expect.equal col.Y.Rows 3 "y rows"

        let buf = Buf()
        col.EncodeColumn(buf)
        Expect.equal buf.WrittenSpan.Length 48 "encoded length"

        let dec = ColPoint()
        let ms = new MemoryStream(buf.WrittenSpan.ToArray())
        dec.DecodeColumn(Reader(ms), 3)

        Expect.equal (dec.Row(0)) { X = 1.0; Y = 2.0 } "row 0"
        Expect.equal (dec.Row(1)) { X = -3.5; Y = 4.5 } "row 1"
        Expect.equal (dec.Row(2)) { X = 0.0; Y = 0.0 } "row 2"

    testCase "Append(x, y) overload matches Append(Point)" <| fun _ ->
        let a = ColPoint()
        let b = ColPoint()
        a.Append({ X = 1.5; Y = -2.5 })
        b.Append(1.5, -2.5)

        let aBuf = Buf()
        let bBuf = Buf()
        a.EncodeColumn(aBuf)
        b.EncodeColumn(bBuf)
        Expect.equal (aBuf.WrittenSpan.ToArray()) (bBuf.WrittenSpan.ToArray()) "same bytes"

    testCase "Reset clears both axes" <| fun _ ->
        let col = ColPoint()
        col.Append({ X = 1.0; Y = 2.0 })
        col.Append({ X = 3.0; Y = 4.0 })
        Expect.equal col.Rows 2 "rows pre-reset"

        col.Reset()
        Expect.equal col.Rows 0 "rows"
        Expect.equal col.X.Rows 0 "x rows"
        Expect.equal col.Y.Rows 0 "y rows"

    testCase "Array(Point) recursively composes" <| fun _ ->
        let col = ColArr<Point>(ColPoint())
        col.Append([| { X = 1.0; Y = 2.0 }; { X = 3.0; Y = 4.0 } |])
        col.Append([| { X = 5.0; Y = 6.0 } |])
        col.Append([||])

        Expect.equal col.Rows 3 "rows"
        Expect.equal col.Type "Array(Point)" "type"

        let buf = Buf()
        col.EncodeColumn(buf)
        let dec = ColArr<Point>(ColPoint())
        let ms = new MemoryStream(buf.WrittenSpan.ToArray())
        dec.DecodeColumn(Reader(ms), 3)

        Expect.equal
            (dec.Row(0))
            [| { X = 1.0; Y = 2.0 }; { X = 3.0; Y = 4.0 } |]
            "row 0"
        Expect.equal (dec.Row(1)) [| { X = 5.0; Y = 6.0 } |] "row 1"
        Expect.equal (dec.Row(2)) ([||] : Point array) "row 2"
]
