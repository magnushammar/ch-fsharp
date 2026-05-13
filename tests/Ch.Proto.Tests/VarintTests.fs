module Ch.Proto.Tests.VarintTests

open System
open Expecto
open Ch.Proto

let private roundtrip (v: uint64) =
    let buf : byte array = Array.zeroCreate Varint.MaxLen
    let n = Varint.write (buf.AsSpan()) v
    Expect.isTrue (n >= 1 && n <= Varint.MaxLen) $"width {n} out of range for {v}"
    match Varint.tryRead (ReadOnlySpan(buf, 0, n)) with
    | ValueSome (struct (decoded, consumed)) ->
        Expect.equal decoded v "decoded value"
        Expect.equal consumed n "consumed bytes"
    | ValueNone ->
        failtestf "failed to decode %d" v

[<Tests>]
let tests = testList "Varint" [
    testList "uvarint roundtrip" [
        for v in [0UL; 1UL; 127UL; 128UL; 255UL; 256UL; 16383UL; 16384UL
                  0xFFFFFFFFUL; 0x100000000UL; 0xFFFFFFFFFFFFFFFFUL] ->
            testCase $"v={v}" <| fun _ -> roundtrip v
    ]

    testCase "encodes 0 as single zero byte" <| fun _ ->
        let buf : byte array = Array.zeroCreate 10
        let n = Varint.write (buf.AsSpan()) 0UL
        Expect.equal n 1 "byte count"
        Expect.equal buf.[0] 0uy "byte value"

    testCase "encodes 127 as single byte" <| fun _ ->
        let buf : byte array = Array.zeroCreate 10
        let n = Varint.write (buf.AsSpan()) 127UL
        Expect.equal n 1 "byte count"
        Expect.equal buf.[0] 127uy "byte value"

    testCase "encodes 128 as two bytes 0x80 0x01" <| fun _ ->
        let buf : byte array = Array.zeroCreate 10
        let n = Varint.write (buf.AsSpan()) 128UL
        Expect.equal n 2 "byte count"
        Expect.equal buf.[0] 0x80uy "byte 0"
        Expect.equal buf.[1] 0x01uy "byte 1"

    testCase "encodes protocol version 54460" <| fun _ ->
        let buf : byte array = Array.zeroCreate 10
        let n = Varint.write (buf.AsSpan()) 54460UL
        Expect.equal n 3 "byte count"
        Expect.equal buf.[0] 0xBCuy "byte 0"
        Expect.equal buf.[1] 0xA9uy "byte 1"
        Expect.equal buf.[2] 0x03uy "byte 2"

    testCase "tryRead returns None on truncated input" <| fun _ ->
        let buf = [| 0x80uy |]
        Expect.equal (Varint.tryRead (ReadOnlySpan(buf))) ValueNone "truncated read"
]
