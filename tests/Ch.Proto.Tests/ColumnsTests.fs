module Ch.Proto.Tests.ColumnsTests

open System
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
    if not (File.Exists p) then
        failwithf "golden fixture not found: %s" p
    p

let private roundtrip<'T
        when 'T : unmanaged
        and 'T : struct
        and 'T : (new : unit -> 'T)
        and 'T :> ValueType
        and 'T : equality
        and 'T : not null>
        (newCol: unit -> ColPrimitive<'T>)
        (rows: int)
        (mkValue: int -> 'T)
        (goldenName: string)
        (expectedTypeName: string) =
    let col = newCol()
    for i in 0 .. rows - 1 do
        col.Append(mkValue i)
        Expect.equal (col.Row(i)) (mkValue i) "row roundtrip"
    Expect.equal col.Rows rows "rows"
    Expect.equal col.Type expectedTypeName "type"

    let buf = Buf()
    col.EncodeColumn(buf)
    let expected = File.ReadAllBytes(goldenPath goldenName)
    Expect.equal (buf.WrittenSpan.ToArray()) expected "encoded bytes"

    let ms = new MemoryStream(expected)
    let r = Reader(ms)
    let dec = newCol()
    dec.DecodeColumn(r, rows)
    Expect.equal dec.Rows rows "decoded rows"
    Expect.equal dec.Type expectedTypeName "decoded type"
    for i in 0 .. rows - 1 do
        Expect.equal (dec.Row(i)) (mkValue i) "decoded row"

    dec.Reset()
    Expect.equal dec.Rows 0 "rows after reset"

    let empty = new MemoryStream(Array.Empty<byte>())
    let er = Reader(empty)
    let zc = newCol()
    zc.DecodeColumn(er, 0)
    Expect.equal zc.Rows 0 "zero-row decode"

    let empBuf = Buf()
    (newCol()).EncodeColumn(empBuf)
    Expect.equal empBuf.Length 0 "zero-row encode"

[<Tests>]
let tests = testList "Columns" [
    testCase "ColInt8 roundtrips against golden" <| fun _ ->
        roundtrip (fun () -> ColInt8() :> ColPrimitive<int8>) 50 (fun i -> int8 i) "col_int8" "Int8"
    testCase "ColInt16 roundtrips against golden" <| fun _ ->
        roundtrip (fun () -> ColInt16() :> ColPrimitive<int16>) 50 (fun i -> int16 i) "col_int16" "Int16"
    testCase "ColInt32 roundtrips against golden" <| fun _ ->
        roundtrip (fun () -> ColInt32() :> ColPrimitive<int32>) 50 (fun i -> int32 i) "col_int32" "Int32"
    testCase "ColInt64 roundtrips against golden" <| fun _ ->
        roundtrip (fun () -> ColInt64() :> ColPrimitive<int64>) 50 (fun i -> int64 i) "col_int64" "Int64"
    testCase "ColUInt8 roundtrips against golden" <| fun _ ->
        roundtrip (fun () -> ColUInt8() :> ColPrimitive<uint8>) 50 (fun i -> uint8 i) "col_uint8" "UInt8"
    testCase "ColUInt16 roundtrips against golden" <| fun _ ->
        roundtrip (fun () -> ColUInt16() :> ColPrimitive<uint16>) 50 (fun i -> uint16 i) "col_uint16" "UInt16"
    testCase "ColUInt32 roundtrips against golden" <| fun _ ->
        roundtrip (fun () -> ColUInt32() :> ColPrimitive<uint32>) 50 (fun i -> uint32 i) "col_uint32" "UInt32"
    testCase "ColUInt64 roundtrips against golden" <| fun _ ->
        roundtrip (fun () -> ColUInt64() :> ColPrimitive<uint64>) 50 (fun i -> uint64 i) "col_uint64" "UInt64"
    testCase "ColFloat32 roundtrips against golden" <| fun _ ->
        roundtrip (fun () -> ColFloat32() :> ColPrimitive<float32>) 50 (fun i -> float32 i) "col_float32" "Float32"
    testCase "ColFloat64 roundtrips against golden" <| fun _ ->
        roundtrip (fun () -> ColFloat64() :> ColPrimitive<float>) 50 (fun i -> float i) "col_float64" "Float64"
    testCase "ColBool roundtrips against golden" <| fun _ ->
        roundtrip (fun () -> ColBool() :> ColPrimitive<bool>) 50 (fun i -> (i % 3) = 0) "col_bool" "Bool"
    testCase "ColInt128 roundtrips against golden" <| fun _ ->
        roundtrip (fun () -> ColInt128() :> ColPrimitive<System.Int128>)
            50 (fun i -> System.Int128(0UL, uint64 i)) "col_int128" "Int128"
    testCase "ColUInt128 roundtrips against golden" <| fun _ ->
        roundtrip (fun () -> ColUInt128() :> ColPrimitive<System.UInt128>)
            50 (fun i -> System.UInt128(0UL, uint64 i)) "col_uint128" "UInt128"
]
