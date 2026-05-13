module Ch.Proto.Tests.ColBFloat16Tests

open System
open System.IO
open Xunit
open Ch.Proto

[<Theory>]
[<InlineData(0.0f, 0us)>]
[<InlineData(1.0f, 0x3F80us)>]
[<InlineData(-1.0f, 0xBF80us)>]
[<InlineData(1.5f, 0x3FC0us)>]
[<InlineData(2.0f, 0x4000us)>]
let ``BFloat16 known-value roundtrip via AppendFloat / RowFloat`` (value: float32, expectedBF: uint16) =
    let col = ColBFloat16()
    col.AppendFloat(value)
    Assert.Equal(expectedBF, col.Row(0))
    Assert.Equal(value, col.RowFloat(0))

[<Fact>]
let ``ColBFloat16 multi-row encode round-trips`` () =
    let col = ColBFloat16()
    let values = [| 0.0f; 1.0f; -1.0f; 2.5f; 3.14f; -0.5f; 1024.0f |]
    for v in values do col.AppendFloat(v)

    Assert.Equal(values.Length, col.Rows)
    Assert.Equal("BFloat16", col.Type)

    let buf = Buf()
    col.EncodeColumn(buf)
    // 2 bytes per row.
    Assert.Equal(values.Length * 2, buf.WrittenSpan.Length)

    let dec = ColBFloat16()
    let ms = new MemoryStream(buf.WrittenSpan.ToArray())
    dec.DecodeColumn(Reader(ms), values.Length)

    for i in 0 .. values.Length - 1 do
        // Round-trip is lossy by design — BFloat16 has 7 mantissa bits, so
        // we compare with a small relative tolerance against the original.
        let dec' = dec.RowFloat(i)
        Assert.True(MathF.Abs(values.[i] - dec') <= MathF.Abs(values.[i]) * 0.01f + 0.01f,
            sprintf "row %d: expected ~%f got %f" i values.[i] dec')

[<Fact>]
let ``BFloat16 preserves zeros and small integers exactly`` () =
    let col = ColBFloat16()
    let values = [| 0.0f; 1.0f; 2.0f; 4.0f; 8.0f; 16.0f; -1.0f; -2.0f |]
    for v in values do col.AppendFloat(v)
    for i in 0 .. values.Length - 1 do
        Assert.Equal(values.[i], col.RowFloat(i))
