module Ch.Proto.Tests.ColumnsTests

open System
open System.IO
open Xunit
open Ch.Proto

/// Locate ch-go's `_golden/` directory via climb from the test bin folder.
/// Tests run from `tests/Ch.Proto.Tests/bin/<cfg>/net10.0/`, repo root is 5 up.
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

/// Shared round-trip for any `ColPrimitive<'T>`. Ports the structure of every
/// `TestColXxx_DecodeColumn` in ch-go (`proto/col_*_gen_test.go`).
let private roundtrip<'T
        when 'T : unmanaged
        and 'T : struct
        and 'T : (new : unit -> 'T)
        and 'T :> ValueType
        and 'T : equality>
        (newCol: unit -> ColPrimitive<'T>)
        (rows: int)
        (mkValue: int -> 'T)
        (goldenName: string)
        (expectedTypeName: string) =
    // Append + Row(i) round-trip.
    let col = newCol()
    for i in 0 .. rows - 1 do
        col.Append(mkValue i)
        Assert.Equal<'T>(mkValue i, col.Row(i))
    Assert.Equal(rows, col.Rows)
    Assert.Equal(expectedTypeName, col.Type)

    // Encode → golden bytes match.
    let buf = Buf()
    col.EncodeColumn(buf)
    let expected = File.ReadAllBytes(goldenPath goldenName)
    Assert.Equal<byte array>(expected, buf.WrittenSpan.ToArray())

    // Decode round-trip from the golden bytes.
    let ms = new MemoryStream(expected)
    let r = Reader(ms)
    let dec = newCol()
    dec.DecodeColumn(r, rows)
    Assert.Equal(rows, dec.Rows)
    Assert.Equal(expectedTypeName, dec.Type)
    for i in 0 .. rows - 1 do
        Assert.Equal<'T>(mkValue i, dec.Row(i))

    // Reset preserves capacity, drops count.
    dec.Reset()
    Assert.Equal(0, dec.Rows)

    // ZeroRows decode is a no-op against an empty reader.
    let empty = new MemoryStream(Array.Empty<byte>())
    let er = Reader(empty)
    let zc = newCol()
    zc.DecodeColumn(er, 0)
    Assert.Equal(0, zc.Rows)

    // ZeroRows encode appends nothing.
    let empBuf = Buf()
    (newCol()).EncodeColumn(empBuf)
    Assert.Equal(0, empBuf.Length)

[<Fact>]
let ``ColInt8 roundtrips against golden`` () =
    roundtrip (fun () -> ColInt8() :> ColPrimitive<int8>) 50 (fun i -> int8 i) "col_int8" "Int8"

[<Fact>]
let ``ColInt16 roundtrips against golden`` () =
    roundtrip (fun () -> ColInt16() :> ColPrimitive<int16>) 50 (fun i -> int16 i) "col_int16" "Int16"

[<Fact>]
let ``ColInt32 roundtrips against golden`` () =
    roundtrip (fun () -> ColInt32() :> ColPrimitive<int32>) 50 (fun i -> int32 i) "col_int32" "Int32"

[<Fact>]
let ``ColInt64 roundtrips against golden`` () =
    roundtrip (fun () -> ColInt64() :> ColPrimitive<int64>) 50 (fun i -> int64 i) "col_int64" "Int64"

[<Fact>]
let ``ColUInt8 roundtrips against golden`` () =
    roundtrip (fun () -> ColUInt8() :> ColPrimitive<uint8>) 50 (fun i -> uint8 i) "col_uint8" "UInt8"

[<Fact>]
let ``ColUInt16 roundtrips against golden`` () =
    roundtrip (fun () -> ColUInt16() :> ColPrimitive<uint16>) 50 (fun i -> uint16 i) "col_uint16" "UInt16"

[<Fact>]
let ``ColUInt32 roundtrips against golden`` () =
    roundtrip (fun () -> ColUInt32() :> ColPrimitive<uint32>) 50 (fun i -> uint32 i) "col_uint32" "UInt32"

[<Fact>]
let ``ColUInt64 roundtrips against golden`` () =
    roundtrip (fun () -> ColUInt64() :> ColPrimitive<uint64>) 50 (fun i -> uint64 i) "col_uint64" "UInt64"

[<Fact>]
let ``ColFloat32 roundtrips against golden`` () =
    roundtrip (fun () -> ColFloat32() :> ColPrimitive<float32>) 50 (fun i -> float32 i) "col_float32" "Float32"

[<Fact>]
let ``ColFloat64 roundtrips against golden`` () =
    roundtrip (fun () -> ColFloat64() :> ColPrimitive<float>) 50 (fun i -> float i) "col_float64" "Float64"

[<Fact>]
let ``ColBool roundtrips against golden`` () =
    // ch-go col_bool_test.go uses `(i%3) == 0`.
    roundtrip (fun () -> ColBool() :> ColPrimitive<bool>) 50 (fun i -> (i % 3) = 0) "col_bool" "Bool"

[<Fact>]
let ``ColInt128 roundtrips against golden`` () =
    // ch-go col_int128_gen_test.go appends `Int128{Low: uint64(i)}`.
    // .NET System.Int128 little-endian layout matches that exactly.
    roundtrip (fun () -> ColInt128() :> ColPrimitive<System.Int128>)
        50 (fun i -> System.Int128(0UL, uint64 i)) "col_int128" "Int128"

[<Fact>]
let ``ColUInt128 roundtrips against golden`` () =
    roundtrip (fun () -> ColUInt128() :> ColPrimitive<System.UInt128>)
        50 (fun i -> System.UInt128(0UL, uint64 i)) "col_uint128" "UInt128"
