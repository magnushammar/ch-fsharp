module Ch.Proto.Tests.ColEnumTests

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

/// Wire bytes for Enum8 are identical to Int8 — 50 rows of `i` little-endian.
/// Uses AppendRaw to skip the name map, matching ch-go's
/// `proto/col_enum8_gen_test.go`.
[<Fact>]
let ``ColEnum8 raw 50-row sample matches col_enum8 golden`` () =
    let col = ColEnum8()
    for i in 0 .. 49 do col.AppendRaw(int8 i)

    Assert.Equal(50, col.Rows)

    let buf = Buf()
    col.EncodeColumn(buf)
    let expected = File.ReadAllBytes(goldenPath "col_enum8")
    Assert.Equal<byte array>(expected, buf.WrittenSpan.ToArray())

[<Fact>]
let ``ColEnum16 raw 50-row sample matches col_enum16 golden`` () =
    let col = ColEnum16()
    for i in 0 .. 49 do col.AppendRaw(int16 i)

    Assert.Equal(50, col.Rows)

    let buf = Buf()
    col.EncodeColumn(buf)
    let expected = File.ReadAllBytes(goldenPath "col_enum16")
    Assert.Equal<byte array>(expected, buf.WrittenSpan.ToArray())

[<Fact>]
let ``ColEnum8 round-trips through name API with pre-declared mapping`` () =
    let col = ColEnum8([| "off", 0y; "on", 1y; "dim", 2y |])
    col.Append("off")
    col.Append("on")
    col.Append("dim")
    col.Append("on")

    Assert.Equal(4, col.Rows)
    Assert.Equal("Enum8('off' = 0, 'on' = 1, 'dim' = 2)", col.Type)

    let buf = Buf()
    col.EncodeColumn(buf)

    let dec = ColEnum8([| "off", 0y; "on", 1y; "dim", 2y |])
    let ms = new MemoryStream(buf.WrittenSpan.ToArray())
    dec.DecodeColumn(Reader(ms), 4)

    Assert.Equal("off", dec.Row(0))
    Assert.Equal("on", dec.Row(1))
    Assert.Equal("dim", dec.Row(2))
    Assert.Equal("on", dec.Row(3))

[<Fact>]
let ``ColEnum8 Infer populates the mapping from a server type string`` () =
    let col = ColEnum8()
    col.Infer("Enum8('idle' = 0, 'running' = 1, 'done' = 2)")
    Assert.Equal("Enum8('idle' = 0, 'running' = 1, 'done' = 2)", col.Type)
    Assert.Equal(0y, col.NameToValue.["idle"])
    Assert.Equal("running", col.ValueToName.[1y])

    col.AppendRaw(2y)
    Assert.Equal("done", col.Row(0))

[<Fact>]
let ``ColEnum16 Infer handles negative wire values`` () =
    let col = ColEnum16()
    col.Infer("Enum16('neg' = -10, 'zero' = 0, 'pos' = 32000)")
    Assert.Equal(-10s, col.NameToValue.["neg"])
    Assert.Equal("pos", col.ValueToName.[32000s])

[<Fact>]
let ``ColEnum8 Append throws for unknown name`` () =
    let col = ColEnum8([| "a", 1y |])
    Assert.Throws<System.Collections.Generic.KeyNotFoundException>(
        fun () -> col.Append("missing")
    ) |> ignore

[<Fact>]
let ``ColEnum8 Reset clears values but preserves mapping`` () =
    let col = ColEnum8([| "a", 1y; "b", 2y |])
    col.Append("a"); col.Append("b")
    Assert.Equal(2, col.Rows)

    col.Reset()
    Assert.Equal(0, col.Rows)
    // Mapping survives, can still append.
    col.Append("a")
    Assert.Equal(1, col.Rows)
    Assert.Equal("a", col.Row(0))

[<Fact>]
let ``ColumnType normalize strips Enum parameters`` () =
    Assert.Equal("Enum8", ColumnType.normalize "Enum8('a' = 1, 'b' = 2)")
    Assert.Equal("Enum16", ColumnType.normalize "Enum16('x' = 10, 'y' = 20)")
    Assert.Equal("Array(Enum8)", ColumnType.normalize "Array(Enum8('a' = 1))")
    Assert.True(ColumnType.isCompatible "Enum8" "Enum8('a' = 1, 'b' = 2)")
    Assert.True(ColumnType.isCompatible "Enum16" "Enum16('foo' = 0)")
