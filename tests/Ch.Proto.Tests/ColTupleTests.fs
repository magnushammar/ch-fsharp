module Ch.Proto.Tests.ColTupleTests

open System
open System.IO
open Xunit
open Ch.Proto

let private goldenPath (name: string) : string =
    let p =
        Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                "reference", "ch-go", "proto", "_golden",
                $"{name}.raw"))
    if not (File.Exists p) then
        failwith $"golden fixture not found: {p}"
    p

/// Port of `proto/col_tuple_test.go: TestColTuple_DecodeColumn`.
/// Tuple(String, Int64) with 50 rows: row i = ("<i>", i).
[<Fact>]
let ``ColTuple<String, Int64> 50-row sample matches col_tuple_str_int64 golden`` () =
    let strs = ColStr()
    let ints = ColInt64()
    for i in 0 .. 49 do
        strs.Append($"<{i}>")
        ints.Append(int64 i)
    let tup = ColTuple([| strs :> IColumnResult; ints :> IColumnResult |])

    Assert.Equal(50, tup.Rows)
    Assert.Equal("Tuple(String, Int64)", tup.Type)

    let buf = Buf()
    tup.EncodeColumn(buf)
    let expected = File.ReadAllBytes(goldenPath "col_tuple_str_int64")
    Assert.Equal<byte array>(expected, buf.WrittenSpan.ToArray())

    let decStrs = ColStr()
    let decInts = ColInt64()
    let dec = ColTuple([| decStrs :> IColumnResult; decInts :> IColumnResult |])
    let ms = new MemoryStream(expected)
    dec.DecodeColumn(Reader(ms), 50)
    Assert.Equal(50, dec.Rows)
    for i in 0 .. 49 do
        Assert.Equal($"<{i}>", decStrs.Row(i))
        Assert.Equal(int64 i, decInts.Row(i))

/// Port of `TestColTuple_DecodeColumn_Named`. Wrap each inner with
/// `ColNamed` to flip the Type string. Wire bytes are unchanged.
[<Fact>]
let ``ColTuple named matches col_tuple_named_str_int64 golden`` () =
    let strs = ColStr()
    let ints = ColInt64()
    for i in 0 .. 49 do
        strs.Append($"<{i}>")
        ints.Append(int64 i)
    let namedStr = ColNamed<string>("strings", strs)
    let namedInt = ColNamed<int64>("ints", ints)
    let tup = ColTuple([| namedStr :> IColumnResult; namedInt :> IColumnResult |])

    Assert.Equal("Tuple(strings String, ints Int64)", tup.Type)

    let buf = Buf()
    tup.EncodeColumn(buf)
    let expected = File.ReadAllBytes(goldenPath "col_tuple_named_str_int64")
    Assert.Equal<byte array>(expected, buf.WrittenSpan.ToArray())

[<Fact>]
let ``ColTuple Reset clears every inner`` () =
    let s = ColStr()
    let i32 = ColInt32()
    s.Append("a"); s.Append("b")
    i32.Append(1); i32.Append(2)
    let tup = ColTuple([| s :> IColumnResult; i32 :> IColumnResult |])
    Assert.Equal(2, tup.Rows)

    tup.Reset()
    Assert.Equal(0, tup.Rows)
    Assert.Equal(0, s.Rows)
    Assert.Equal(0, i32.Rows)

[<Fact>]
let ``ColTuple empty inner array reports zero rows`` () =
    let tup = ColTuple([||])
    Assert.Equal(0, tup.Rows)
    Assert.Equal("Tuple()", tup.Type)

[<Fact>]
let ``ColTuple with LowCardinality inner forwards state`` () =
    // Encoded bytes need to round-trip including the per-block state header
    // of the LowCardinality member.
    let s = ColStr()
    let lc = ColLowCardinality<string>(ColStr())
    s.Append("hello")
    s.Append("world")
    lc.Append("x"); lc.Append("y")
    let tup = ColTuple([| s :> IColumnResult; lc :> IColumnResult |])

    Assert.Equal(2, tup.Rows)
    Assert.Equal("Tuple(String, LowCardinality(String))", tup.Type)

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

    Assert.Equal("hello", decS.Row(0))
    Assert.Equal("world", decS.Row(1))
    Assert.Equal("x", decLc.Row(0))
    Assert.Equal("y", decLc.Row(1))

[<Fact>]
let ``ColNamed exposes IColumnOf<'T> through the wrapper`` () =
    let inner = ColInt32()
    let n = ColNamed<int32>("count", inner)
    Assert.Equal("count Int32", n.Type)

    let asCol = n :> IColumnOf<int32>
    asCol.Append(42)
    asCol.Append(100)
    Assert.Equal(42, asCol.Row(0))
    Assert.Equal(100, asCol.Row(1))
    Assert.Equal(2, inner.Rows)  // writes pass through
