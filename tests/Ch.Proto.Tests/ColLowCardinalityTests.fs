module Ch.Proto.Tests.ColLowCardinalityTests

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

/// Port of `proto/col_low_cardinality_test.go: TestColLowCardinality_DecodeColumn/Str`.
[<Fact>]
let ``ColLowCardinality<string> 25 rows over 3 unique strings (k=8) matches golden`` () =
    let values = [| "neo"; "trinity"; "morpheus" |]
    let lc = ColLowCardinality<string>(ColStr())

    for i in 0 .. 24 do
        lc.Append(values.[i % 3])

    Assert.Equal("LowCardinality(String)", lc.Type)

    let buf = Buf()
    lc.EncodeColumn(buf)
    let expected = File.ReadAllBytes(goldenPath "col_low_cardinality_i_str_k_8")
    Assert.Equal<byte array>(expected, buf.WrittenSpan.ToArray())

    // Decode round-trip.
    let dec = ColLowCardinality<string>(ColStr())
    let ms = new MemoryStream(expected)
    dec.DecodeColumn(Reader(ms), 25)

    Assert.Equal(25, dec.Rows)
    Assert.Equal(3, dec.DictRows)
    Assert.Equal(1, dec.KeyWidth)
    for i in 0 .. 24 do
        Assert.Equal(values.[i % 3], dec.Row(i))

/// Dictionary materialisation is the load-bearing perf departure from
/// ch-go (see plans/DESIGN_CHOICES.md §8). Verify that dict access is
/// keyWidth-bounded — Row(i) hits the dictArray, not inner.Row.
[<Fact>]
let ``ColLowCardinality<string> exposes Inner and Dictionary for fast iteration`` () =
    let lc = ColLowCardinality<string>(ColStr())
    for i in 0 .. 9 do lc.Append("only-one")

    let buf = Buf()
    lc.EncodeColumn(buf)
    let ms = new MemoryStream(buf.WrittenSpan.ToArray())
    let dec = ColLowCardinality<string>(ColStr())
    dec.DecodeColumn(Reader(ms), 10)

    // Single unique → dictRows = 1, keys are 10 zero bytes.
    Assert.Equal(1, dec.DictRows)
    Assert.Equal(10, dec.Rows)
    Assert.Equal(1, dec.KeyWidth)
    let dict = dec.Dictionary
    Assert.Equal("only-one", dict.[0])
    for k in dec.Keys.ToArray() do
        Assert.Equal(0uy, k)

[<Fact>]
let ``ColLowCardinality<string> empty column encodes to empty bytes`` () =
    let lc = ColLowCardinality<string>(ColStr())
    let buf = Buf()
    lc.EncodeColumn(buf)
    Assert.Equal(0, buf.Length)
    Assert.Equal(0, lc.Rows)

[<Fact>]
let ``ColLowCardinality<string> Reset preserves capacity but clears rows`` () =
    let lc = ColLowCardinality<string>(ColStr())
    for s in [| "a"; "b"; "a"; "c" |] do lc.Append(s)
    let buf = Buf()
    lc.EncodeColumn(buf)
    Assert.Equal(4, lc.Rows)

    lc.Reset()
    Assert.Equal(0, lc.Rows)
    // Re-encode after reset is a no-op.
    let buf2 = Buf()
    lc.EncodeColumn(buf2)
    Assert.Equal(0, buf2.Length)

[<Fact>]
let ``ColLowCardinality<int32> over numeric T also works`` () =
    let lc = ColLowCardinality<int32>(ColInt32())
    for i in 0 .. 49 do lc.Append(i % 5)
    let buf = Buf()
    lc.EncodeColumn(buf)

    Assert.Equal("LowCardinality(Int32)", lc.Type)

    let dec = ColLowCardinality<int32>(ColInt32())
    let ms = new MemoryStream(buf.WrittenSpan.ToArray())
    dec.DecodeColumn(Reader(ms), 50)

    Assert.Equal(50, dec.Rows)
    Assert.Equal(5, dec.DictRows)
    for i in 0 .. 49 do
        Assert.Equal(i % 5, dec.Row(i))

[<Fact>]
let ``ColLowCardinality DecodeState rejects bad version`` () =
    let lc = ColLowCardinality<string>(ColStr())
    // Synthesise a bad version (2 instead of 1).
    let bad : byte array = Array.zeroCreate 8
    bad.[0] <- 2uy
    let ms = new MemoryStream(bad)
    Assert.Throws<InvalidDataException>(fun () -> lc.DecodeState(Reader(ms)))
    |> ignore
