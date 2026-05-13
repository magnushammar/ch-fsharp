module Ch.Proto.Tests.CityHash128Tests

open System
open Xunit
open Ch.Proto

[<Theory>]
// Vectors generated from go-faster/city@v1.0.1 (`bench/go/cmd/cityhash-vectors`).
// Covers every size bucket the algorithm branches on: 0, 1–7 (small loop),
// 8–15 (8-byte seed path), 16, 17–127 (chMurmur), 128 (loop boundary),
// 129+ (loop + tail), large multi-iteration.
[<InlineData("",                                                                                                                                                                       0x3df09dfc64c09a2bUL, 0x3cb540c392e51e29UL)>]
[<InlineData("61",                                                                                                                                                                     0xd27139a1afe01ad0UL, 0xfd7e8ee2e4c86cf6UL)>]
[<InlineData("31323334353637",                                                                                                                                                         0x8e8777e875b97597UL, 0x8852d1373eb8453bUL)>]
[<InlineData("3132333435363738",                                                                                                                                                       0x1d7757a6dd6fd9f3UL, 0x7282a0f9c3847a18UL)>]
[<InlineData("313233343536373839303132333435",                                                                                                                                         0xa7aca61ef9ad6954UL, 0x69a896e8852a795bUL)>]
[<InlineData("31323334353637383930313233343536",                                                                                                                                       0xe17786877893c752UL, 0x983b6db3a5dadd06UL)>]
[<InlineData("3132333435363738393031323334353637",                                                                                                                                     0xac84e9f3d9b62f76UL, 0x20c27ed832020c63UL)>]
[<InlineData("3132333435363738393031323334353637383930313233343536373839303132",                                                                                                       0x8cb78f22ff8ead4dUL, 0x48d4f29067ec71c3UL)>]
[<InlineData("6162636465666768696a6b6c6d6e6f707172737475767778797a6162636465666768696a6b6c6d6e6f707172737475767778797a6162636465666768696a6b",                                         0xb116211798a4ab4eUL, 0xf194672eac53ff1eUL)>]
[<InlineData("5453444751744d3237536d6a4c306e61464d71635133455473594b624462724265496a",                                                                                                 0x5a568f460b784901UL, 0x8194e9a0cefb22fdUL)>]
let ``CH128 matches go-faster/city`` (hex: string) (expectedLow: uint64) (expectedHigh: uint64) =
    let bytes = Convert.FromHexString hex
    let actual = CityHash128.hash (ReadOnlySpan(bytes))
    Assert.Equal(expectedLow, actual.Low)
    Assert.Equal(expectedHigh, actual.High)

[<Theory>]
// Long inputs that exercise the 128-byte main loop. Generated from
// `aaab...xyzab...` repeated. Hex elided here — we synthesize the input.
[<InlineData(127, 0x68097f8b3f484befUL, 0xe102c231b9d4ae1bUL)>]
[<InlineData(128, 0x887eb72f491fc81dUL, 0xef3d6e8ab9443fefUL)>]
[<InlineData(129, 0x660b1d294a2157f0UL, 0x3d983b4d117d63d0UL)>]
[<InlineData(256, 0x3ba8f6d8e2506548UL, 0x5fb35050a88367a4UL)>]
[<InlineData(1000, 0xe000415fe0102c24UL, 0x75d9ff0bc9cf92b8UL)>]
let ``CH128 matches on synthetic large inputs`` (length: int) (expectedLow: uint64) (expectedHigh: uint64) =
    let bytes : byte array = Array.init length (fun i -> byte ((int 'a') + (i % 26)))
    let actual = CityHash128.hash (ReadOnlySpan(bytes))
    Assert.Equal(expectedLow, actual.Low)
    Assert.Equal(expectedHigh, actual.High)
