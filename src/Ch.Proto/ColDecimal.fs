namespace Ch.Proto

open System

/// `Decimal32` — Int32 mantissa. ClickHouse stores `Decimal(P, S)` here when
/// P ≤ 9. Wire-identical to `ColInt32`; the Type string is the only
/// difference. Scale is the caller's concern — use the `Decimal` module for
/// conversion to `System.Decimal`.
[<Sealed>] type ColDecimal32() = inherit ColPrimitive<int32>("Decimal32")

/// `Decimal64` — Int64 mantissa. P ≤ 18.
[<Sealed>] type ColDecimal64() = inherit ColPrimitive<int64>("Decimal64")

/// `Decimal128` — Int128 mantissa. P ≤ 38. Note: `System.Decimal` is 96-bit
/// (≤ 28-29 decimal digits) so a Decimal128 with more than 28 digits will
/// overflow when converted via the `Decimal` module — work with the raw
/// `Int128` value in those cases.
[<Sealed>] type ColDecimal128() = inherit ColPrimitive<Int128>("Decimal128")

/// `Decimal256` — `Int256` mantissa. P ≤ 76. Same overflow caveat as
/// Decimal128 applies — `System.Decimal` only holds 28-29 digits.
[<Sealed>] type ColDecimal256() = inherit ColPrimitive<Int256>("Decimal256")

/// Scale helpers for converting between raw integer mantissas and
/// `System.Decimal`. `System.Decimal` is base-10 so dividing by `10^scale` is
/// exact (no rounding). Scale must be in 0..28.
///
/// For Decimal128 values whose mantissa exceeds 2^96 (~ 28 digits), use the
/// raw `Int128` accessor on `ColDecimal128` instead — `System.Decimal` will
/// overflow.
module Decimal =
    let private pow10 : decimal array =
        Array.init 29 (fun i ->
            let mutable v = 1m
            for _ in 1 .. i do v <- v * 10m
            v)

    /// Decode raw 32-bit mantissa as a scaled `System.Decimal`.
    let fromInt32 (raw: int32) (scale: int) : decimal =
        decimal raw / pow10.[scale]

    /// Decode raw 64-bit mantissa as a scaled `System.Decimal`.
    let fromInt64 (raw: int64) (scale: int) : decimal =
        decimal raw / pow10.[scale]

    /// Encode a `System.Decimal` as raw 32-bit mantissa. Truncates toward zero.
    let toInt32 (v: decimal) (scale: int) : int32 =
        int32 (v * pow10.[scale])

    /// Encode a `System.Decimal` as raw 64-bit mantissa. Truncates toward zero.
    let toInt64 (v: decimal) (scale: int) : int64 =
        int64 (v * pow10.[scale])
