module Ch.Proto.Tests.ColAutoTests

open System
open System.IO
open Expecto
open Ch.Proto

/// Columns ColAuto is expected to handle. Used by the facet guardrail tests
/// to assert every typed column wires up the right facet interfaces.
let private facetCheckTargets : (string * IColumnResult) list = [
    ("Int8",       ColInt8()        :> _)
    ("Int16",      ColInt16()       :> _)
    ("Int32",      ColInt32()       :> _)
    ("Int64",      ColInt64()       :> _)
    ("Int128",     ColInt128()      :> _)
    ("Int256",     ColInt256()      :> _)
    ("UInt8",      ColUInt8()       :> _)
    ("UInt16",     ColUInt16()      :> _)
    ("UInt32",     ColUInt32()      :> _)
    ("UInt64",     ColUInt64()      :> _)
    ("UInt128",    ColUInt128()     :> _)
    ("UInt256",    ColUInt256()     :> _)
    ("Float32",    ColFloat32()     :> _)
    ("Float64",    ColFloat64()     :> _)
    ("Bool",       ColBool()        :> _)
    ("BFloat16",   ColBFloat16()    :> _)
    ("String",     ColStr()         :> _)
    ("Date",       ColDate()        :> _)
    ("Date32",     ColDate32()      :> _)
    ("DateTime",   ColDateTime()    :> _)
    ("DateTime64", ColDateTime64(3) :> _)
    ("IPv4",       ColIPv4()        :> _)
    ("Enum8",      ColEnum8()       :> _)
    ("Enum16",     ColEnum16()      :> _)
    ("Interval",   ColInterval()    :> _)
    ("JSON",       ColJSONStr()     :> _)
    ("Point",      ColPoint()       :> _)
    ("Nothing",    ColNothing()     :> _)
    ("Decimal32",  ColDecimal32()   :> _)
    ("Decimal64",  ColDecimal64()   :> _)
    ("Decimal128", ColDecimal128()  :> _)
    ("Decimal256", ColDecimal256()  :> _)
]

[<Tests>]
let tests = testList "ColAuto" [
    testList "build dispatches scalar primitives" [
        for typeStr in
            [ "Int32"; "Int64"; "UInt8"; "UInt256"; "Float64"; "Bool"
              "String"; "JSON"; "Date"; "Date32"; "DateTime"
              "UUID"; "IPv4"; "IPv6"; "Point"; "Nothing"; "BFloat16" ] ->
            testCase typeStr <| fun _ ->
                let col = ColAuto.build typeStr
                Expect.equal col.Type typeStr "type"
    ]

    testList "build downcasts Decimal(P, S) to the right width" [
        for (input, expected) in
            [ "Decimal(9, 2)", "Decimal32"
              "Decimal(18, 4)", "Decimal64"
              "Decimal(38, 8)", "Decimal128"
              "Decimal(76, 0)", "Decimal256"
              "Decimal32", "Decimal32"
              "Decimal64", "Decimal64" ] ->
            testCase input <| fun _ ->
                let col = ColAuto.build input
                Expect.equal col.Type expected "type"
    ]

    testCase "build Enum8 parses the mapping" <| fun _ ->
        let col = ColAuto.build "Enum8('off' = 0, 'on' = 1, 'dim' = 2)"
        let en = col :?> ColEnum8
        Expect.equal (en.NameToValue.["off"]) 0y "off"
        Expect.equal (en.ValueToName.[2y]) "dim" "2y"
        Expect.equal col.Type "Enum8('off' = 0, 'on' = 1, 'dim' = 2)" "type"

    testCase "build DateTime64 parses precision and strips timezone" <| fun _ ->
        let col1 = ColAuto.build "DateTime64(3)"
        let dt1 = col1 :?> ColDateTime64
        Expect.equal dt1.Precision 3 "precision 3"

        let col2 = ColAuto.build "DateTime64(9, 'UTC')"
        let dt2 = col2 :?> ColDateTime64
        Expect.equal dt2.Precision 9 "precision 9"

    testCase "build FixedString(N) parses width" <| fun _ ->
        let col = ColAuto.build "FixedString(8)"
        Expect.equal col.Type "FixedString(8)" "type"
        let fs = col :?> ColFixedStr
        Expect.equal fs.ElemSize 8 "elem size"

    testCase "build Interval picks the scale" <| fun _ ->
        let col = ColAuto.build "IntervalDay"
        let iv = col :?> ColInterval
        Expect.equal iv.Scale Day "scale"

    testCase "build constructs Array(T) — ColArr<'T>" <| fun _ ->
        match ColAuto.build "Array(Int32)" with
        | :? ColArr<int32> -> ()
        | other -> failtestf "expected ColArr<int32>, got %A" (other.GetType())

        match ColAuto.build "Array(Float64)" with
        | :? ColArr<float> -> ()
        | other -> failtestf "expected ColArr<float>, got %A" (other.GetType())

        match ColAuto.build "Array(String)" with
        | :? ColArr<string> -> ()
        | other -> failtestf "expected ColArr<string>, got %A" (other.GetType())

    testCase "build constructs Nullable(T) — ColNullable<'T>" <| fun _ ->
        match ColAuto.build "Nullable(Int32)" with
        | :? ColNullable<int32> -> ()
        | other -> failtestf "expected ColNullable<int32>, got %A" (other.GetType())

        match ColAuto.build "Nullable(String)" with
        | :? ColNullable<string> -> ()
        | other -> failtestf "expected ColNullable<string>, got %A" (other.GetType())

        match ColAuto.build "Nullable(Nothing)" with
        | :? ColNullable<Nothing> -> ()
        | other -> failtestf "expected ColNullable<Nothing>, got %A" (other.GetType())

    testCase "build constructs LowCardinality(T) — ColLowCardinality<'T>" <| fun _ ->
        match ColAuto.build "LowCardinality(String)" with
        | :? ColLowCardinality<string> -> ()
        | other -> failtestf "expected ColLowCardinality<string>, got %A" (other.GetType())

        match ColAuto.build "LowCardinality(Int32)" with
        | :? ColLowCardinality<int32> -> ()
        | other -> failtestf "expected ColLowCardinality<int32>, got %A" (other.GetType())

    testCase "build constructs Tuple(T1, T2, ...) — ColTuple" <| fun _ ->
        match ColAuto.build "Tuple(String, Int32)" with
        | :? ColTuple as t ->
            Expect.equal t.Type "Tuple(String, Int32)" "type"
        | other -> failtestf "expected ColTuple, got %A" (other.GetType())

        match ColAuto.build "Tuple(String, Tuple(Int32, Float64))" with
        | :? ColTuple as t ->
            Expect.equal t.Type "Tuple(String, Tuple(Int32, Float64))" "nested type"
        | other -> failtestf "expected ColTuple, got %A" (other.GetType())

    testCase "build constructs Map(String, String) hardcoded" <| fun _ ->
        match ColAuto.build "Map(String, String)" with
        | :? ColMap<string, string> -> ()
        | other -> failtestf "expected ColMap<string, string>, got %A" (other.GetType())

    testCase "build Array(Array(Int32)) recursive — ColArr<int32 array>" <| fun _ ->
        match ColAuto.build "Array(Array(Int32))" with
        | :? ColArr<int32 array> -> ()
        | other -> failtestf "expected ColArr<int32 array>, got %A" (other.GetType())

    testCase "build Array(Nullable(Int32)) recursive — ColArr<int32 voption>" <| fun _ ->
        match ColAuto.build "Array(Nullable(Int32))" with
        | :? ColArr<int32 voption> -> ()
        | other -> failtestf "expected ColArr<int32 voption>, got %A" (other.GetType())

    testCase "build Array(LowCardinality(String)) recursive — ColArr<string>" <| fun _ ->
        match ColAuto.build "Array(LowCardinality(String))" with
        | :? ColArr<string> -> ()
        | other -> failtestf "expected ColArr<string>, got %A" (other.GetType())

    testCase "build rejects general Map(K, V) — only (String, String) hardcoded" <| fun _ ->
        Expect.throwsT<NotSupportedException>
            (fun () -> ColAuto.build "Map(String, Int32)" |> ignore)
            "general Map"

    testCase "facet guardrail: every typed column implements IArrayable" <| fun _ ->
        for (name, c) in facetCheckTargets do
            Expect.isTrue (c :? IArrayable)
                (sprintf "%s does not implement IArrayable" name)

    testCase "facet guardrail: every typed column implements INullable" <| fun _ ->
        for (name, c) in facetCheckTargets do
            Expect.isTrue (c :? INullable)
                (sprintf "%s does not implement INullable" name)

    testCase "facet guardrail: every typed column (except Nothing) implements ILowCardinality" <| fun _ ->
        for (name, c) in facetCheckTargets do
            if name <> "Nothing" then
                Expect.isTrue (c :? ILowCardinality)
                    (sprintf "%s does not implement ILowCardinality" name)

    testCase "ColAuto column instance round-trips via Infer + IColumnResult" <| fun _ ->
        let auto = ColAuto()
        let inferable = auto :> IColumnResult
        (auto :> IInferable).Infer("Int32")

        Expect.equal inferable.Type "Int32" "type"
        Expect.equal inferable.Rows 0 "rows"

        let inner = auto.Inner.Value :?> ColInt32
        inner.Append(10); inner.Append(20); inner.Append(30)

        let buf = Buf()
        inferable.EncodeColumn(buf)
        Expect.equal buf.WrittenSpan.Length 12 "encoded length"

        let other = ColAuto()
        (other :> IInferable).Infer("Int32")
        let ms = new MemoryStream(buf.WrittenSpan.ToArray())
        (other :> IColumnResult).DecodeColumn(Reader(ms), 3)
        let innerB = other.Inner.Value :?> ColInt32
        Expect.equal (innerB.Row(0)) 10 "row 0"
        Expect.equal (innerB.Row(1)) 20 "row 1"
        Expect.equal (innerB.Row(2)) 30 "row 2"

    testCase "ColumnType.normalize strips DateTime / DateTime64 timezones" <| fun _ ->
        Expect.equal (ColumnType.normalize "DateTime('UTC')") "DateTime" "DateTime"
        Expect.equal (ColumnType.normalize "DateTime64(3, 'UTC')") "DateTime64(3)" "DateTime64"
        Expect.equal
            (ColumnType.normalize "Array(DateTime('America/New_York'))")
            "Array(DateTime)"
            "Array(DateTime)"
]
