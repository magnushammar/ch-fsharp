module Ch.Proto.Tests.ColAutoTests

open System
open System.IO
open Expecto
open Ch.Proto

[<Tests>]
let tests = testList "ColAuto" [
    testList "build dispatches scalar primitives" [
        for typeStr in
            [ "Int32"; "Int64"; "UInt8"; "UInt256"; "Float64"; "Bool"
              "String"; "JSON"; "Date"; "Date32"; "DateTime"
              "UUID"; "IPv4"; "IPv6"; "Point"; "Nothing"; "BFloat16" ] ->
            testCase typeStr <| fun _ ->
                let col = ColAuto.build typeStr
                Expect.equal col.Type typeStr "type"
    ]

    testList "build downcasts Decimal(P, S) to the right width" [
        for (input, expected) in
            [ "Decimal(9, 2)", "Decimal32"
              "Decimal(18, 4)", "Decimal64"
              "Decimal(38, 8)", "Decimal128"
              "Decimal(76, 0)", "Decimal256"
              "Decimal32", "Decimal32"
              "Decimal64", "Decimal64" ] ->
            testCase input <| fun _ ->
                let col = ColAuto.build input
                Expect.equal col.Type expected "type"
    ]

    testCase "build Enum8 parses the mapping" <| fun _ ->
        let col = ColAuto.build "Enum8('off' = 0, 'on' = 1, 'dim' = 2)"
        let en = col :?> ColEnum8
        Expect.equal (en.NameToValue.["off"]) 0y "off"
        Expect.equal (en.ValueToName.[2y]) "dim" "2y"
        Expect.equal col.Type "Enum8('off' = 0, 'on' = 1, 'dim' = 2)" "type"

    testCase "build DateTime64 parses precision and strips timezone" <| fun _ ->
        let col1 = ColAuto.build "DateTime64(3)"
        let dt1 = col1 :?> ColDateTime64
        Expect.equal dt1.Precision 3 "precision 3"

        let col2 = ColAuto.build "DateTime64(9, 'UTC')"
        let dt2 = col2 :?> ColDateTime64
        Expect.equal dt2.Precision 9 "precision 9"

    testCase "build FixedString(N) parses width" <| fun _ ->
        let col = ColAuto.build "FixedString(8)"
        Expect.equal col.Type "FixedString(8)" "type"
        let fs = col :?> ColFixedStr
        Expect.equal fs.ElemSize 8 "elem size"

    testCase "build Interval picks the scale" <| fun _ ->
        let col = ColAuto.build "IntervalDay"
        let iv = col :?> ColInterval
        Expect.equal iv.Scale Day "scale"

    testCase "build rejects composite types" <| fun _ ->
        Expect.throwsT<NotSupportedException>
            (fun () -> ColAuto.build "Array(Int32)" |> ignore)
            "Array"
        Expect.throwsT<NotSupportedException>
            (fun () -> ColAuto.build "Nullable(String)" |> ignore)
            "Nullable"

    testCase "ColAuto column instance round-trips via Infer + IColumnResult" <| fun _ ->
        let auto = ColAuto()
        let inferable = auto :> IColumnResult
        (auto :> IInferable).Infer("Int32")

        Expect.equal inferable.Type "Int32" "type"
        Expect.equal inferable.Rows 0 "rows"

        let inner = auto.Inner.Value :?> ColInt32
        inner.Append(10); inner.Append(20); inner.Append(30)

        let buf = Buf()
        inferable.EncodeColumn(buf)
        Expect.equal buf.WrittenSpan.Length 12 "encoded length"

        let other = ColAuto()
        (other :> IInferable).Infer("Int32")
        let ms = new MemoryStream(buf.WrittenSpan.ToArray())
        (other :> IColumnResult).DecodeColumn(Reader(ms), 3)
        let innerB = other.Inner.Value :?> ColInt32
        Expect.equal (innerB.Row(0)) 10 "row 0"
        Expect.equal (innerB.Row(1)) 20 "row 1"
        Expect.equal (innerB.Row(2)) 30 "row 2"

    testCase "ColumnType.normalize strips DateTime / DateTime64 timezones" <| fun _ ->
        Expect.equal (ColumnType.normalize "DateTime('UTC')") "DateTime" "DateTime"
        Expect.equal (ColumnType.normalize "DateTime64(3, 'UTC')") "DateTime64(3)" "DateTime64"
        Expect.equal
            (ColumnType.normalize "Array(DateTime('America/New_York'))")
            "Array(DateTime)"
            "Array(DateTime)"
]
