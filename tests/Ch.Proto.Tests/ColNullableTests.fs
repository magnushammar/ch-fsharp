module Ch.Proto.Tests.ColNullableTests

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
let tests = testList "ColNullable" [
    testCase "<string> 10-row sample matches col_nullable_str golden" <| fun _ ->
        let col = ColNullable<string>(ColStr())
        let values = [|
            ValueSome "value1"; ValueSome "value2"; ValueNone; ValueSome "value3"
            ValueNone; ValueSome ""; ValueSome ""; ValueSome "value4"
            ValueNone; ValueSome "value54"
        |]
        for v in values do col.Append(v)

        Expect.equal col.Rows 10 "rows"
        Expect.equal col.Type "Nullable(String)" "type"

        let buf = Buf()
        col.EncodeColumn(buf)
        let expected = File.ReadAllBytes(goldenPath "col_nullable_str")
        Expect.equal (buf.WrittenSpan.ToArray()) expected "encoded bytes"

        let dec = ColNullable<string>(ColStr())
        let ms = new MemoryStream(expected)
        dec.DecodeColumn(Reader(ms), 10)
        Expect.equal dec.Rows 10 "decoded rows"
        for i in 0 .. 9 do
            Expect.equal (dec.Row(i)) values.[i] "row"

    testCase "IsNull flag matches null rows" <| fun _ ->
        let col = ColNullable<string>(ColStr())
        col.Append(ValueSome "a"); col.Append(ValueNone)
        col.Append(ValueSome "b"); col.Append(ValueNone)
        Expect.isFalse (col.IsNull(0)) "row 0"
        Expect.isTrue  (col.IsNull(1)) "row 1"
        Expect.isFalse (col.IsNull(2)) "row 2"
        Expect.isTrue  (col.IsNull(3)) "row 3"

    testCase "<int32> round-trips with numeric inner" <| fun _ ->
        let col = ColNullable<int32>(ColInt32())
        let values = [| ValueSome 1; ValueNone; ValueSome 3; ValueSome 0; ValueNone; ValueSome -42 |]
        for v in values do col.Append(v)

        let buf = Buf()
        col.EncodeColumn(buf)
        let ms = new MemoryStream(buf.WrittenSpan.ToArray())
        let dec = ColNullable<int32>(ColInt32())
        dec.DecodeColumn(Reader(ms), values.Length)

        Expect.equal dec.Type "Nullable(Int32)" "type"
        for i in 0 .. values.Length - 1 do
            Expect.equal (dec.Row(i)) values.[i] "row"

    testCase "Reset clears both nulls and inner" <| fun _ ->
        let col = ColNullable<string>(ColStr())
        col.Append(ValueSome "x"); col.Append(ValueNone)
        Expect.equal col.Rows 2 "rows pre-reset"
        col.Reset()
        Expect.equal col.Rows 0 "rows"
        Expect.equal col.NullsCount 0 "nulls"
        Expect.equal col.Inner.Rows 0 "inner"
]
