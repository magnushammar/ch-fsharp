module Ch.Proto.Tests.ColBFloat16Tests

open System
open System.IO
open Expecto
open Ch.Proto

[<Tests>]
let tests = testList "ColBFloat16" [
    testList "known-value roundtrip" [
        for (value, expectedBF) in
            [ (0.0f, 0us); (1.0f, 0x3F80us); (-1.0f, 0xBF80us)
              (1.5f, 0x3FC0us); (2.0f, 0x4000us) ] ->
            testCase $"v={value}" <| fun _ ->
                let col = ColBFloat16()
                col.AppendFloat(value)
                Expect.equal (col.Row(0)) expectedBF "raw BFloat16 bits"
                Expect.equal (col.RowFloat(0)) value "RowFloat round-trip"
    ]

    testCase "multi-row encode round-trips" <| fun _ ->
        let col = ColBFloat16()
        let values = [| 0.0f; 1.0f; -1.0f; 2.5f; 3.14f; -0.5f; 1024.0f |]
        for v in values do col.AppendFloat(v)

        Expect.equal col.Rows values.Length "rows"
        Expect.equal col.Type "BFloat16" "type"

        let buf = Buf()
        col.EncodeColumn(buf)
        Expect.equal buf.WrittenSpan.Length (values.Length * 2) "encoded length"

        let dec = ColBFloat16()
        let ms = new MemoryStream(buf.WrittenSpan.ToArray())
        dec.DecodeColumn(Reader(ms), values.Length)

        for i in 0 .. values.Length - 1 do
            let dec' = dec.RowFloat(i)
            Expect.isTrue
                (MathF.Abs(values.[i] - dec') <= MathF.Abs(values.[i]) * 0.01f + 0.01f)
                (sprintf "row %d: expected ~%f got %f" i values.[i] dec')

    testCase "preserves zeros and small integers exactly" <| fun _ ->
        let col = ColBFloat16()
        let values = [| 0.0f; 1.0f; 2.0f; 4.0f; 8.0f; 16.0f; -1.0f; -2.0f |]
        for v in values do col.AppendFloat(v)
        for i in 0 .. values.Length - 1 do
            Expect.equal (col.RowFloat(i)) values.[i] "row"
]
