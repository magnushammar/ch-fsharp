module Ch.Proto.Tests.ColTupleTests

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
let tests = testList "ColTuple" [
    testCase "<String, Int64> 50-row sample matches col_tuple_str_int64 golden" <| fun _ ->
        let strs = ColStr()
        let ints = ColInt64()
        for i in 0 .. 49 do
            strs.Append($"<{i}>")
            ints.Append(int64 i)
        let tup = ColTuple([| strs :> IColumnResult; ints :> IColumnResult |])

        Expect.equal tup.Rows 50 "rows"
        Expect.equal tup.Type "Tuple(String, Int64)" "type"

        let buf = Buf()
        tup.EncodeColumn(buf)
        let expected = File.ReadAllBytes(goldenPath "col_tuple_str_int64")
        Expect.equal (buf.WrittenSpan.ToArray()) expected "encoded bytes"

        let decStrs = ColStr()
        let decInts = ColInt64()
        let dec = ColTuple([| decStrs :> IColumnResult; decInts :> IColumnResult |])
        let ms = new MemoryStream(expected)
        dec.DecodeColumn(Reader(ms), 50)
        Expect.equal dec.Rows 50 "decoded rows"
        for i in 0 .. 49 do
            Expect.equal (decStrs.Row(i)) $"<{i}>" "str row"
            Expect.equal (decInts.Row(i)) (int64 i) "int row"

    testCase "named matches col_tuple_named_str_int64 golden" <| fun _ ->
        let strs = ColStr()
        let ints = ColInt64()
        for i in 0 .. 49 do
            strs.Append($"<{i}>")
            ints.Append(int64 i)
        let namedStr = ColNamed<string>("strings", strs)
        let namedInt = ColNamed<int64>("ints", ints)
        let tup = ColTuple([| namedStr :> IColumnResult; namedInt :> IColumnResult |])

        Expect.equal tup.Type "Tuple(strings String, ints Int64)" "type"

        let buf = Buf()
        tup.EncodeColumn(buf)
        let expected = File.ReadAllBytes(goldenPath "col_tuple_named_str_int64")
        Expect.equal (buf.WrittenSpan.ToArray()) expected "encoded bytes"

    testCase "Reset clears every inner" <| fun _ ->
        let s = ColStr()
        let i32 = ColInt32()
        s.Append("a"); s.Append("b")
        i32.Append(1); i32.Append(2)
        let tup = ColTuple([| s :> IColumnResult; i32 :> IColumnResult |])
        Expect.equal tup.Rows 2 "rows pre-reset"

        tup.Reset()
        Expect.equal tup.Rows 0 "rows"
        Expect.equal s.Rows 0 "s rows"
        Expect.equal i32.Rows 0 "i32 rows"

    testCase "empty inner array reports zero rows" <| fun _ ->
        let tup = ColTuple([||])
        Expect.equal tup.Rows 0 "rows"
        Expect.equal tup.Type "Tuple()" "type"

    testCase "with LowCardinality inner forwards state" <| fun _ ->
        let s = ColStr()
        let lc = ColLowCardinality<string>(ColStr())
        s.Append("hello"); s.Append("world")
        lc.Append("x"); lc.Append("y")
        let tup = ColTuple([| s :> IColumnResult; lc :> IColumnResult |])

        Expect.equal tup.Rows 2 "rows"
        Expect.equal tup.Type "Tuple(String, LowCardinality(String))" "type"

        let buf = Buf()
        (tup :> IStatefulColumn).EncodeState(buf)
        tup.EncodeColumn(buf)

        let decS = ColStr()
        let decLc = ColLowCardinality<string>(ColStr())
        let dec = ColTuple([| decS :> IColumnResult; decLc :> IColumnResult |])
        let ms = new MemoryStream(buf.WrittenSpan.ToArray())
        let r = Reader(ms)
        (dec :> IStatefulColumn).DecodeState(r)
        dec.DecodeColumn(r, 2)

        Expect.equal (decS.Row(0)) "hello" "s row 0"
        Expect.equal (decS.Row(1)) "world" "s row 1"
        Expect.equal (decLc.Row(0)) "x" "lc row 0"
        Expect.equal (decLc.Row(1)) "y" "lc row 1"

    testCase "ColNamed exposes IColumnOf<'T> through the wrapper" <| fun _ ->
        let inner = ColInt32()
        let n = ColNamed<int32>("count", inner)
        Expect.equal n.Type "count Int32" "type"

        let asCol = n :> IColumnOf<int32>
        asCol.Append(42)
        asCol.Append(100)
        Expect.equal (asCol.Row(0)) 42 "row 0"
        Expect.equal (asCol.Row(1)) 100 "row 1"
        Expect.equal inner.Rows 2 "inner rows"
]
