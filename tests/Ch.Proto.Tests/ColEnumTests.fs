module Ch.Proto.Tests.ColEnumTests

open System
open System.Collections.Generic
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
let tests = testList "ColEnum" [
    testCase "ColEnum8 raw 50-row sample matches col_enum8 golden" <| fun _ ->
        let col = ColEnum8()
        for i in 0 .. 49 do col.AppendRaw(int8 i)
        Expect.equal col.Rows 50 "rows"

        let buf = Buf()
        col.EncodeColumn(buf)
        let expected = File.ReadAllBytes(goldenPath "col_enum8")
        Expect.equal (buf.WrittenSpan.ToArray()) expected "encoded bytes"

    testCase "ColEnum16 raw 50-row sample matches col_enum16 golden" <| fun _ ->
        let col = ColEnum16()
        for i in 0 .. 49 do col.AppendRaw(int16 i)
        Expect.equal col.Rows 50 "rows"

        let buf = Buf()
        col.EncodeColumn(buf)
        let expected = File.ReadAllBytes(goldenPath "col_enum16")
        Expect.equal (buf.WrittenSpan.ToArray()) expected "encoded bytes"

    testCase "ColEnum8 round-trips through name API with pre-declared mapping" <| fun _ ->
        let col = ColEnum8([| "off", 0y; "on", 1y; "dim", 2y |])
        col.Append("off"); col.Append("on"); col.Append("dim"); col.Append("on")

        Expect.equal col.Rows 4 "rows"
        Expect.equal col.Type "Enum8('off' = 0, 'on' = 1, 'dim' = 2)" "type"

        let buf = Buf()
        col.EncodeColumn(buf)

        let dec = ColEnum8([| "off", 0y; "on", 1y; "dim", 2y |])
        let ms = new MemoryStream(buf.WrittenSpan.ToArray())
        dec.DecodeColumn(Reader(ms), 4)

        Expect.equal (dec.Row(0)) "off" "row 0"
        Expect.equal (dec.Row(1)) "on" "row 1"
        Expect.equal (dec.Row(2)) "dim" "row 2"
        Expect.equal (dec.Row(3)) "on" "row 3"

    testCase "ColEnum8 Infer populates the mapping from a server type string" <| fun _ ->
        let col = ColEnum8()
        col.Infer("Enum8('idle' = 0, 'running' = 1, 'done' = 2)")
        Expect.equal col.Type "Enum8('idle' = 0, 'running' = 1, 'done' = 2)" "type"
        Expect.equal (col.NameToValue.["idle"]) 0y "idle"
        Expect.equal (col.ValueToName.[1y]) "running" "1y"

        col.AppendRaw(2y)
        Expect.equal (col.Row(0)) "done" "row 0"

    testCase "ColEnum16 Infer handles negative wire values" <| fun _ ->
        let col = ColEnum16()
        col.Infer("Enum16('neg' = -10, 'zero' = 0, 'pos' = 32000)")
        Expect.equal (col.NameToValue.["neg"]) -10s "neg"
        Expect.equal (col.ValueToName.[32000s]) "pos" "pos"

    testCase "ColEnum8 Append throws for unknown name" <| fun _ ->
        let col = ColEnum8([| "a", 1y |])
        Expect.throwsT<KeyNotFoundException>
            (fun () -> col.Append("missing"))
            "missing name"

    testCase "ColEnum8 Reset clears values but preserves mapping" <| fun _ ->
        let col = ColEnum8([| "a", 1y; "b", 2y |])
        col.Append("a"); col.Append("b")
        Expect.equal col.Rows 2 "rows pre-reset"

        col.Reset()
        Expect.equal col.Rows 0 "rows"
        col.Append("a")
        Expect.equal col.Rows 1 "rows post-reappend"
        Expect.equal (col.Row(0)) "a" "row 0"

    testCase "ColumnType.normalize strips Enum parameters" <| fun _ ->
        Expect.equal (ColumnType.normalize "Enum8('a' = 1, 'b' = 2)") "Enum8" "enum8"
        Expect.equal (ColumnType.normalize "Enum16('x' = 10, 'y' = 20)") "Enum16" "enum16"
        Expect.equal (ColumnType.normalize "Array(Enum8('a' = 1))") "Array(Enum8)" "array of enum"
        Expect.isTrue (ColumnType.isCompatible "Enum8" "Enum8('a' = 1, 'b' = 2)") "enum8 compat"
        Expect.isTrue (ColumnType.isCompatible "Enum16" "Enum16('foo' = 0)") "enum16 compat"

    testCase "ColEnum8.Infer handles commas inside quoted names" <| fun _ ->
        let col = ColEnum8()
        col.Infer("Enum8('a,b' = 1, 'c' = 2)")
        Expect.equal col.NameToValue.["a,b"] 1y "quoted-comma name"
        Expect.equal col.NameToValue.["c"] 2y "plain name"
        Expect.equal col.ValueToName.[1y] "a,b" "reverse map"

    testCase "ColEnum8.Infer handles = inside quoted names" <| fun _ ->
        let col = ColEnum8()
        col.Infer("Enum8('a=b' = 1, 'normal' = 2)")
        Expect.equal col.NameToValue.["a=b"] 1y "quoted-= name"
        Expect.equal col.NameToValue.["normal"] 2y "plain name"

    testCase "ColEnum16.Infer handles commas inside quoted names" <| fun _ ->
        let col = ColEnum16()
        col.Infer("Enum16('x,y' = 100, 'z' = 200)")
        Expect.equal col.NameToValue.["x,y"] 100s "quoted-comma name"
        Expect.equal col.NameToValue.["z"] 200s "plain name"

    testCase "ColAuto.build now handles Enum8 with quoted commas in names" <| fun _ ->
        // The full chain: ColAuto.build → Enum8 branch → asInfer → Infer.
        // Both the outer splitOnTopLevelCommas (in ColAuto) and the
        // inner splitTopLevel (in ColEnum.Infer) must be quote-aware.
        let col = ColAuto.build "Enum8('a,b' = 1, 'c,d' = 2)"
        let en = col :?> ColEnum8
        Expect.equal en.NameToValue.["a,b"] 1y "first quoted-comma"
        Expect.equal en.NameToValue.["c,d"] 2y "second quoted-comma"
]
