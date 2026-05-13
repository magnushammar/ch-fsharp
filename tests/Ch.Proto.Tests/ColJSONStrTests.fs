module Ch.Proto.Tests.ColJSONStrTests

open System
open System.IO
open Xunit
open Ch.Proto

[<Fact>]
let ``ColJSONStr round-trips a small set of JSON documents`` () =
    let col = ColJSONStr()
    col.Append("""{"a":1}""")
    col.Append("""{"hello":"world"}""")
    col.Append("[]")

    Assert.Equal(3, col.Rows)
    Assert.Equal("JSON", col.Type)

    let buf = Buf()
    (col :> IStatefulColumn).EncodeState(buf)
    col.EncodeColumn(buf)

    let dec = ColJSONStr()
    let ms = new MemoryStream(buf.WrittenSpan.ToArray())
    let r = Reader(ms)
    (dec :> IStatefulColumn).DecodeState(r)
    dec.DecodeColumn(r, 3)

    Assert.Equal("""{"a":1}""", dec.Row(0))
    Assert.Equal("""{"hello":"world"}""", dec.Row(1))
    Assert.Equal("[]", dec.Row(2))

[<Fact>]
let ``ColJSONStr DecodeState rejects unknown version`` () =
    let dec = ColJSONStr()
    // Construct a state header with version=999.
    let buf = Buf()
    buf.PutUInt64(999UL)
    let ms = new MemoryStream(buf.WrittenSpan.ToArray())
    Assert.Throws<FormatException>(
        fun () -> (dec :> IStatefulColumn).DecodeState(Reader(ms))
    ) |> ignore

[<Fact>]
let ``ColJSONStr Reset clears rows`` () =
    let col = ColJSONStr()
    col.Append("""{}""")
    col.Append("""null""")
    Assert.Equal(2, col.Rows)

    col.Reset()
    Assert.Equal(0, col.Rows)
