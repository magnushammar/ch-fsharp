module Ch.Proto.Tests.ColArrTests

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

/// Port of `proto/col_arr_test.go: TestColArr_DecodeColumn`.
/// 5 rows of Int8 arrays where row i has i+2 elements with formula
/// `10 + j*2 + 3*i`.
[<Fact>]
let ``ColArr<int8> 5-row sample matches col_arr_int8_manual golden`` () =
    let col = ColArr<int8>(ColInt8())
    let values =
        [|
            for i in 0 .. 4 ->
                [| for j in 0 .. i + 1 -> int8 (10 + j * 2 + 3 * i) |]
        |]
    for v in values do col.Append(v)

    Assert.Equal(5, col.Rows)
    Assert.Equal("Array(Int8)", col.Type)

    let buf = Buf()
    col.EncodeColumn(buf)
    let expected = File.ReadAllBytes(goldenPath "col_arr_int8_manual")
    Assert.Equal<byte array>(expected, buf.WrittenSpan.ToArray())

    let dec = ColArr<int8>(ColInt8())
    let ms = new MemoryStream(expected)
    dec.DecodeColumn(Reader(ms), 5)
    Assert.Equal(5, dec.Rows)
    for i in 0 .. 4 do
        Assert.Equal<int8 array>(values.[i], dec.Row(i))

[<Fact>]
let ``ColArr<string> stores variable-length string rows`` () =
    let col = ColArr<string>(ColStr())
    col.Append([| "alpha"; "beta" |])
    col.Append([| "gamma" |])
    col.Append([||])
    col.Append([| "delta"; "epsilon"; "zeta" |])

    Assert.Equal(4, col.Rows)
    Assert.Equal("Array(String)", col.Type)

    let buf = Buf()
    col.EncodeColumn(buf)
    let ms = new MemoryStream(buf.WrittenSpan.ToArray())
    let dec = ColArr<string>(ColStr())
    dec.DecodeColumn(Reader(ms), 4)

    Assert.Equal<string array>([| "alpha"; "beta" |], dec.Row(0))
    Assert.Equal<string array>([| "gamma" |], dec.Row(1))
    Assert.Equal<string array>([||], dec.Row(2))
    Assert.Equal<string array>([| "delta"; "epsilon"; "zeta" |], dec.Row(3))

[<Fact>]
let ``ColArr<int32[]> recursive Array(Array(Int32))`` () =
    // ColArr<int32 array> — Array(Array(Int32)). Inner is itself a ColArr.
    let inner = ColArr<int32>(ColInt32())
    let outer = ColArr<int32 array>(inner)
    outer.Append([|
        [| 1; 2 |]
        [| 3; 4; 5 |]
    |])
    outer.Append([|
        [| 10 |]
    |])
    outer.Append([||])

    Assert.Equal(3, outer.Rows)
    Assert.Equal("Array(Array(Int32))", outer.Type)

    let buf = Buf()
    outer.EncodeColumn(buf)
    let ms = new MemoryStream(buf.WrittenSpan.ToArray())
    let dec = ColArr<int32 array>(ColArr<int32>(ColInt32()))
    dec.DecodeColumn(Reader(ms), 3)

    let row0 = dec.Row(0)
    Assert.Equal(2, row0.Length)
    Assert.Equal<int32 array>([| 1; 2 |], row0.[0])
    Assert.Equal<int32 array>([| 3; 4; 5 |], row0.[1])
    Assert.Equal<int32 array>([| [| 10 |] |], dec.Row(1))
    Assert.Equal<int32 array array>([||], dec.Row(2))

[<Fact>]
let ``ColArr Reset clears offsets and inner`` () =
    let col = ColArr<int32>(ColInt32())
    col.Append([| 1; 2; 3 |])
    col.Append([| 4; 5 |])
    Assert.Equal(2, col.Rows)

    col.Reset()
    Assert.Equal(0, col.Rows)
    Assert.Equal(0, col.Offsets.Rows)
    Assert.Equal(0, col.Inner.Rows)
