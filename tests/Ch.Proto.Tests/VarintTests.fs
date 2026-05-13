module Ch.Proto.Tests.VarintTests

open System
open Xunit
open Ch.Proto

let private roundtrip (v: uint64) =
    let buf : byte array = Array.zeroCreate Varint.MaxLen
    let n = Varint.write (buf.AsSpan()) v
    Assert.InRange(n, 1, Varint.MaxLen)
    match Varint.tryRead (ReadOnlySpan(buf, 0, n)) with
    | ValueSome (struct (decoded, consumed)) ->
        Assert.Equal(v, decoded)
        Assert.Equal(n, consumed)
    | ValueNone ->
        Assert.Fail $"failed to decode {v}"

[<Theory>]
[<InlineData(0UL)>]
[<InlineData(1UL)>]
[<InlineData(127UL)>]
[<InlineData(128UL)>]
[<InlineData(255UL)>]
[<InlineData(256UL)>]
[<InlineData(16383UL)>]
[<InlineData(16384UL)>]
[<InlineData(0xFFFFFFFFUL)>]
[<InlineData(0x100000000UL)>]
[<InlineData(0xFFFFFFFFFFFFFFFFUL)>]
let ``uvarint roundtrip`` (v: uint64) = roundtrip v

[<Fact>]
let ``encodes 0 as single zero byte`` () =
    let buf : byte array = Array.zeroCreate 10
    let n = Varint.write (buf.AsSpan()) 0UL
    Assert.Equal(1, n)
    Assert.Equal(0uy, buf.[0])

[<Fact>]
let ``encodes 127 as single byte`` () =
    let buf : byte array = Array.zeroCreate 10
    let n = Varint.write (buf.AsSpan()) 127UL
    Assert.Equal(1, n)
    Assert.Equal(127uy, buf.[0])

[<Fact>]
let ``encodes 128 as two bytes 0x80 0x01`` () =
    let buf : byte array = Array.zeroCreate 10
    let n = Varint.write (buf.AsSpan()) 128UL
    Assert.Equal(2, n)
    Assert.Equal(0x80uy, buf.[0])
    Assert.Equal(0x01uy, buf.[1])

[<Fact>]
let ``encodes protocol version 54460`` () =
    // ch-go protocol version is sent everywhere as a uvarint.
    let buf : byte array = Array.zeroCreate 10
    let n = Varint.write (buf.AsSpan()) 54460UL
    Assert.Equal(3, n)
    // 54460 = 0xD4BC → uvarint: 0xBC 0xA9 0x03
    Assert.Equal(0xBCuy, buf.[0])
    Assert.Equal(0xA9uy, buf.[1])
    Assert.Equal(0x03uy, buf.[2])

[<Fact>]
let ``tryRead returns None on truncated input`` () =
    // 0x80 alone has continuation bit set but no follow-on byte.
    let buf = [| 0x80uy |]
    Assert.Equal(ValueNone, Varint.tryRead (ReadOnlySpan(buf)))
