module Ch.Proto.Tests.ColFixedTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
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
let tests = testList "ColFixed" [
    testCase "ColUUID round-trips against col_uuid golden" <| fun _ ->
        let col = ColUUID()
        for i in 0 .. 49 do
            let bytes : byte array = Array.zeroCreate 16
            bytes.[0] <- byte i
            col.AppendBytes(ReadOnlySpan(bytes))
        Expect.equal col.Rows 50 "rows"
        Expect.equal col.Type "UUID" "type"

        let buf = Buf()
        col.EncodeColumn(buf)
        let expected = File.ReadAllBytes(goldenPath "col_uuid")
        Expect.equal (buf.WrittenSpan.ToArray()) expected "encoded bytes"

        let ms = new MemoryStream(expected)
        let dec = ColUUID()
        dec.DecodeColumn(Reader(ms), 50)
        Expect.equal dec.Rows 50 "decoded rows"
        for i in 0 .. 49 do
            let row = dec.RowSpan(i)
            Expect.equal row.[0] (byte i) "row byte 0"
            for j in 1 .. 15 do Expect.equal row.[j] 0uy "row tail byte"

    testCase "ColFixedStr(32) round-trips against col_fixed_str golden" <| fun _ ->
        let input = [| "foo"; "bar"; "ClickHouse"; "one"; ""; "1" |]
        let col = ColFixedStr(32)
        use sha = SHA256.Create()
        for s in input do
            let hash = sha.ComputeHash(Encoding.UTF8.GetBytes(s))
            col.AppendBytes(ReadOnlySpan(hash))
        Expect.equal col.Rows input.Length "rows"
        Expect.equal col.Type "FixedString(32)" "type"

        let buf = Buf()
        col.EncodeColumn(buf)
        let expected = File.ReadAllBytes(goldenPath "col_fixed_str")
        Expect.equal (buf.WrittenSpan.ToArray()) expected "encoded bytes"

        let ms = new MemoryStream(expected)
        let dec = ColFixedStr(32)
        dec.DecodeColumn(Reader(ms), input.Length)
        for i in 0 .. input.Length - 1 do
            let row = dec.RowSpan(i).ToArray()
            let expectedHash = sha.ComputeHash(Encoding.UTF8.GetBytes(input.[i]))
            Expect.equal row expectedHash "decoded row"

    testCase "ColFixedStr rejects wrong-size append" <| fun _ ->
        let col = ColFixedStr(4)
        Expect.throwsT<ArgumentException>
            (fun () -> col.AppendBytes(ReadOnlySpan([| 1uy; 2uy; 3uy |])))
            "size mismatch"

    testCase "ColIPv6 round-trips against col_ipv6 golden" <| fun _ ->
        let col = ColIPv6()
        for i in 0 .. 49 do
            let bytes : byte array = Array.zeroCreate 16
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(
                bytes.AsSpan(0, 8), uint64 i)
            col.AppendBytes(ReadOnlySpan(bytes))
        Expect.equal col.Rows 50 "rows"
        Expect.equal col.Type "IPv6" "type"

        let buf = Buf()
        col.EncodeColumn(buf)
        let expected = File.ReadAllBytes(goldenPath "col_ipv6")
        Expect.equal (buf.WrittenSpan.ToArray()) expected "encoded bytes"

        let ms = new MemoryStream(expected)
        let dec = ColIPv6()
        dec.DecodeColumn(Reader(ms), 50)
        Expect.equal dec.Rows 50 "decoded rows"

    testCase "ColIPv6 AppendIP / RowIP round-trips" <| fun _ ->
        let col = ColIPv6()
        let ip = System.Net.IPAddress.Parse("2001:db8::1")
        col.AppendIP(ip)
        Expect.equal (col.RowIP(0)) ip "ip"
]
