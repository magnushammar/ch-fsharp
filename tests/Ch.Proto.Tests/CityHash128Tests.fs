module Ch.Proto.Tests.CityHash128Tests

open System
open Expecto
open Ch.Proto

// Vectors generated from go-faster/city@v1.0.1.
let private goldenHex = [
    "",                                                                                                                                                                       0x3df09dfc64c09a2bUL, 0x3cb540c392e51e29UL
    "61",                                                                                                                                                                     0xd27139a1afe01ad0UL, 0xfd7e8ee2e4c86cf6UL
    "31323334353637",                                                                                                                                                         0x8e8777e875b97597UL, 0x8852d1373eb8453bUL
    "3132333435363738",                                                                                                                                                       0x1d7757a6dd6fd9f3UL, 0x7282a0f9c3847a18UL
    "313233343536373839303132333435",                                                                                                                                         0xa7aca61ef9ad6954UL, 0x69a896e8852a795bUL
    "31323334353637383930313233343536",                                                                                                                                       0xe17786877893c752UL, 0x983b6db3a5dadd06UL
    "3132333435363738393031323334353637",                                                                                                                                     0xac84e9f3d9b62f76UL, 0x20c27ed832020c63UL
    "3132333435363738393031323334353637383930313233343536373839303132",                                                                                                       0x8cb78f22ff8ead4dUL, 0x48d4f29067ec71c3UL
    "6162636465666768696a6b6c6d6e6f707172737475767778797a6162636465666768696a6b6c6d6e6f707172737475767778797a6162636465666768696a6b",                                         0xb116211798a4ab4eUL, 0xf194672eac53ff1eUL
    "5453444751744d3237536d6a4c306e61464d71635133455473594b624462724265496a",                                                                                                 0x5a568f460b784901UL, 0x8194e9a0cefb22fdUL
]

let private goldenSynthetic = [
    127, 0x68097f8b3f484befUL, 0xe102c231b9d4ae1bUL
    128, 0x887eb72f491fc81dUL, 0xef3d6e8ab9443fefUL
    129, 0x660b1d294a2157f0UL, 0x3d983b4d117d63d0UL
    256, 0x3ba8f6d8e2506548UL, 0x5fb35050a88367a4UL
    1000, 0xe000415fe0102c24UL, 0x75d9ff0bc9cf92b8UL
]

[<Tests>]
let tests = testList "CityHash128" [
    testList "matches go-faster/city" [
        for (hex, lo, hi) in goldenHex ->
            testCase $"hex={hex.Length}b" <| fun _ ->
                let bytes = Convert.FromHexString hex
                let actual = CityHash128.hash (ReadOnlySpan(bytes))
                Expect.equal actual.Low lo "low"
                Expect.equal actual.High hi "high"
    ]

    testList "matches on synthetic large inputs" [
        for (length, lo, hi) in goldenSynthetic ->
            testCase $"len={length}" <| fun _ ->
                let bytes : byte array = Array.init length (fun i -> byte ((int 'a') + (i % 26)))
                let actual = CityHash128.hash (ReadOnlySpan(bytes))
                Expect.equal actual.Low lo "low"
                Expect.equal actual.High hi "high"
    ]
]
