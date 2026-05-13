module Ch.Proto.Tests.ColFixedTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
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

// ─── ColUUID ─────────────────────────────────────────────────

[<Fact>]
let ``ColUUID round-trips against col_uuid golden`` () =
    // ch-go test: `uuid.UUID{byte(i)}` — 16-byte array with first byte = i, rest 0.
    let col = ColUUID()
    for i in 0 .. 49 do
        let bytes : byte array = Array.zeroCreate 16
        bytes.[0] <- byte i
        col.AppendBytes(ReadOnlySpan(bytes))
    Assert.Equal(50, col.Rows)
    Assert.Equal("UUID", col.Type)

    let buf = Buf()
    col.EncodeColumn(buf)
    let expected = File.ReadAllBytes(goldenPath "col_uuid")
    Assert.Equal<byte array>(expected, buf.WrittenSpan.ToArray())

    let ms = new MemoryStream(expected)
    let r = Reader(ms)
    let dec = ColUUID()
    dec.DecodeColumn(r, 50)
    Assert.Equal(50, dec.Rows)
    // After decode the in-memory representation should match the original.
    for i in 0 .. 49 do
        let row = dec.RowSpan(i)
        Assert.Equal(byte i, row.[0])
        for j in 1 .. 15 do Assert.Equal(0uy, row.[j])

// ─── ColFixedStr ─────────────────────────────────────────────

[<Fact>]
let ``ColFixedStr(32) round-trips against col_fixed_str golden`` () =
    // ch-go test: SHA256 of 6 input strings.
    let input = [| "foo"; "bar"; "ClickHouse"; "one"; ""; "1" |]
    let col = ColFixedStr(32)
    use sha = SHA256.Create()
    for s in input do
        let bytes = Encoding.UTF8.GetBytes(s)
        let hash = sha.ComputeHash(bytes)
        col.AppendBytes(ReadOnlySpan(hash))
    Assert.Equal(input.Length, col.Rows)
    Assert.Equal("FixedString(32)", col.Type)

    let buf = Buf()
    col.EncodeColumn(buf)
    let expected = File.ReadAllBytes(goldenPath "col_fixed_str")
    Assert.Equal<byte array>(expected, buf.WrittenSpan.ToArray())

    let ms = new MemoryStream(expected)
    let r = Reader(ms)
    let dec = ColFixedStr(32)
    dec.DecodeColumn(r, input.Length)
    for i in 0 .. input.Length - 1 do
        let row = dec.RowSpan(i).ToArray()
        let expected = sha.ComputeHash(Encoding.UTF8.GetBytes(input.[i]))
        Assert.Equal<byte array>(expected, row)

[<Fact>]
let ``ColFixedStr rejects wrong-size append`` () =
    let col = ColFixedStr(4)
    Assert.Throws<ArgumentException>(fun () ->
        col.AppendBytes(ReadOnlySpan([| 1uy; 2uy; 3uy |])))

// ─── ColIPv6 ─────────────────────────────────────────────────

[<Fact>]
let ``ColIPv6 round-trips against col_ipv6 golden`` () =
    // ch-go test: IPv6FromInt(i) — first 8 bytes are big-endian uint64(i), rest 0.
    let col = ColIPv6()
    for i in 0 .. 49 do
        let bytes : byte array = Array.zeroCreate 16
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(
            bytes.AsSpan(0, 8), uint64 i)
        col.AppendBytes(ReadOnlySpan(bytes))
    Assert.Equal(50, col.Rows)
    Assert.Equal("IPv6", col.Type)

    let buf = Buf()
    col.EncodeColumn(buf)
    let expected = File.ReadAllBytes(goldenPath "col_ipv6")
    Assert.Equal<byte array>(expected, buf.WrittenSpan.ToArray())

    let ms = new MemoryStream(expected)
    let r = Reader(ms)
    let dec = ColIPv6()
    dec.DecodeColumn(r, 50)
    Assert.Equal(50, dec.Rows)

[<Fact>]
let ``ColIPv6 AppendIP / RowIP round-trips`` () =
    let col = ColIPv6()
    let ip = System.Net.IPAddress.Parse("2001:db8::1")
    col.AppendIP(ip)
    Assert.Equal(ip, col.RowIP(0))
