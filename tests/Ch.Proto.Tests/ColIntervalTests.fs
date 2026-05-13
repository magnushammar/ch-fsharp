module Ch.Proto.Tests.ColIntervalTests

open System
open System.IO
open Expecto
open Ch.Proto

[<Tests>]
let tests = testList "ColInterval" [
    testList "Infer accepts every documented scale" [
        for typeStr in
            ["IntervalSecond"; "IntervalMinute"; "IntervalHour"; "IntervalDay"
             "IntervalWeek"; "IntervalMonth"; "IntervalQuarter"; "IntervalYear"] ->
            testCase typeStr <| fun _ ->
                let col = ColInterval()
                col.Infer(typeStr)
                Expect.equal col.Type typeStr "type"
    ]

    testCase "round-trips Int64 wire" <| fun _ ->
        let col = ColInterval(Day)
        col.Append({ Scale = Day; Value = 5L })
        col.Append({ Scale = Day; Value = -3L })
        col.Append({ Scale = Day; Value = 0L })

        Expect.equal col.Rows 3 "rows"
        Expect.equal col.Type "IntervalDay" "type"

        let buf = Buf()
        col.EncodeColumn(buf)
        Expect.equal buf.WrittenSpan.Length 24 "encoded length"

        let dec = ColInterval(Day)
        let ms = new MemoryStream(buf.WrittenSpan.ToArray())
        dec.DecodeColumn(Reader(ms), 3)

        Expect.equal (dec.Row(0)) { Scale = Day; Value = 5L } "row 0"
        Expect.equal (dec.Row(1)) { Scale = Day; Value = -3L } "row 1"
        Expect.equal (dec.Row(2)) { Scale = Day; Value = 0L } "row 2"

    testCase "Append rejects mismatched scale" <| fun _ ->
        let col = ColInterval(Hour)
        Expect.throwsT<ArgumentException>
            (fun () -> col.Append({ Scale = Day; Value = 1L }))
            "mismatched scale"

    testCase "Reset clears values, preserves scale" <| fun _ ->
        let col = ColInterval(Minute)
        col.Append({ Scale = Minute; Value = 30L })
        col.Append({ Scale = Minute; Value = 60L })
        Expect.equal col.Rows 2 "rows pre-reset"

        col.Reset()
        Expect.equal col.Rows 0 "rows"
        Expect.equal col.Scale Minute "scale preserved"
        Expect.equal col.Type "IntervalMinute" "type preserved"

    testCase "Infer with bad type throws" <| fun _ ->
        let col = ColInterval()
        Expect.throwsT<FormatException>
            (fun () -> col.Infer("NotAnInterval"))
            "bad type"

    testCase "IntervalScale string conversion is bijective" <| fun _ ->
        for s in [Second; Minute; Hour; Day; Week; Month; Quarter; Year] do
            let str = IntervalScale.toTypeString s
            Expect.equal (IntervalScale.fromTypeString str) s "bijection"
]
