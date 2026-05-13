module Ch.Proto.Tests.ColMapTests

open System
open System.Collections.Generic
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

/// Port of `proto/col_map_test.go: TestColMapGolden`. Two rows, each with a
/// single string→string pair, so byte output is deterministic regardless of
/// dictionary iteration order.
[<Fact>]
let ``ColMap<string, string> golden bytes match col_map_of_str_str`` () =
    let col = ColMap<string, string>(ColStr(), ColStr())
    let m1 = Dictionary<string, string>()
    m1.["foo"] <- "bar"
    let m2 = Dictionary<string, string>()
    m2.["like"] <- "100"
    col.Append(m1)
    col.Append(m2)

    Assert.Equal(2, col.Rows)
    Assert.Equal("Map(String, String)", col.Type)

    let buf = Buf()
    col.EncodeColumn(buf)
    let expected = File.ReadAllBytes(goldenPath "col_map_of_str_str")
    Assert.Equal<byte array>(expected, buf.WrittenSpan.ToArray())

[<Fact>]
let ``ColMap<string, string> AppendKV preserves order and round-trips`` () =
    let col = ColMap<string, string>(ColStr(), ColStr())
    col.AppendKV([|
        KeyValuePair("foo", "bar")
        KeyValuePair("baz", "hello")
    |])
    col.AppendKV([|
        KeyValuePair("like", "100")
        KeyValuePair("dislike", "200")
    |])

    Assert.Equal(2, col.Rows)
    Assert.Equal(2, col.RowLen(0))
    Assert.Equal(2, col.RowLen(1))

    let buf = Buf()
    col.EncodeColumn(buf)

    let dec = ColMap<string, string>(ColStr(), ColStr())
    let ms = new MemoryStream(buf.WrittenSpan.ToArray())
    dec.DecodeColumn(Reader(ms), 2)

    let kv0 = dec.RowKV(0)
    Assert.Equal(2, kv0.Length)
    Assert.Equal(KeyValuePair("foo", "bar"), kv0.[0])
    Assert.Equal(KeyValuePair("baz", "hello"), kv0.[1])

    let kv1 = dec.RowKV(1)
    Assert.Equal(2, kv1.Length)
    Assert.Equal(KeyValuePair("like", "100"), kv1.[0])
    Assert.Equal(KeyValuePair("dislike", "200"), kv1.[1])

    let row0 = dec.Row(0)
    Assert.Equal("bar", row0.["foo"])
    Assert.Equal("hello", row0.["baz"])

[<Fact>]
let ``ColMap<string, int32> typed values round-trip`` () =
    let col = ColMap<string, int32>(ColStr(), ColInt32())
    col.AppendKV([|
        KeyValuePair("a", 1)
        KeyValuePair("b", 2)
        KeyValuePair("c", 3)
    |])
    col.AppendKV([|
        KeyValuePair("x", 10)
    |])
    col.AppendKV([||])

    Assert.Equal(3, col.Rows)
    Assert.Equal("Map(String, Int32)", col.Type)

    let buf = Buf()
    col.EncodeColumn(buf)
    let dec = ColMap<string, int32>(ColStr(), ColInt32())
    let ms = new MemoryStream(buf.WrittenSpan.ToArray())
    dec.DecodeColumn(Reader(ms), 3)

    Assert.Equal(3, dec.RowLen(0))
    Assert.Equal(1, dec.RowLen(1))
    Assert.Equal(0, dec.RowLen(2))
    Assert.Equal(1, dec.Row(0).["a"])
    Assert.Equal(10, dec.Row(1).["x"])
    Assert.Equal(0, dec.Row(2).Count)

[<Fact>]
let ``ColMap empty round-trip skips body`` () =
    // n=0 short-circuits decode entirely.
    let dec = ColMap<string, string>(ColStr(), ColStr())
    let ms = new MemoryStream([||])
    dec.DecodeColumn(Reader(ms), 0)
    Assert.Equal(0, dec.Rows)

[<Fact>]
let ``ColMap Reset clears offsets and inners`` () =
    let col = ColMap<string, string>(ColStr(), ColStr())
    col.AppendKV([| KeyValuePair("foo", "bar"); KeyValuePair("baz", "hello") |])
    Assert.Equal(1, col.Rows)

    col.Reset()
    Assert.Equal(0, col.Rows)
    Assert.Equal(0, col.Offsets.Rows)
    Assert.Equal(0, col.Keys.Rows)
    Assert.Equal(0, col.Values.Rows)

[<Fact>]
let ``ColMap<string, int32 array> recursive Map(String, Array(Int32))`` () =
    let col = ColMap<string, int32 array>(ColStr(), ColArr<int32>(ColInt32()))
    col.AppendKV([|
        KeyValuePair("evens", [| 2; 4; 6 |])
        KeyValuePair("odds", [| 1; 3 |])
    |])
    col.AppendKV([|
        KeyValuePair("empty", [||])
    |])

    Assert.Equal(2, col.Rows)
    Assert.Equal("Map(String, Array(Int32))", col.Type)

    let buf = Buf()
    col.EncodeColumn(buf)
    let dec = ColMap<string, int32 array>(ColStr(), ColArr<int32>(ColInt32()))
    let ms = new MemoryStream(buf.WrittenSpan.ToArray())
    dec.DecodeColumn(Reader(ms), 2)

    let kv0 = dec.RowKV(0)
    Assert.Equal<int32 array>([| 2; 4; 6 |], kv0.[0].Value)
    Assert.Equal<int32 array>([| 1; 3 |], kv0.[1].Value)
    Assert.Equal<int32 array>([||], dec.RowKV(1).[0].Value)
