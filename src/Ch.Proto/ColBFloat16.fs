namespace Ch.Proto

open System

/// `BFloat16` — Brain Floating-Point, 16-bit format with **1 sign bit, 8
/// exponent bits, 7 mantissa bits**. Wire-identical to `UInt16` (2 LE bytes
/// per row); semantics differ from .NET's `Half` (IEEE 754 binary16, which
/// has 5 exponent + 10 mantissa bits) — they do NOT share a bit layout.
///
/// We inherit `ColPrimitive<uint16>` for the raw-storage hot path and add
/// `AppendFloat` / `RowFloat` helpers to convert to/from `float32`. The
/// raw `Append(uint16)` is still available for callers who already hold
/// BFloat16-encoded values. ch-go reference: `proto/col_bfloat16.go`.
[<Sealed>]
type ColBFloat16() =
    inherit ColPrimitive<uint16>("BFloat16")

    /// Convert IEEE-754 float32 → BFloat16. Round-to-nearest-even via the
    /// classic "add half a low bit before truncating" trick.
    static let f32ToBF16 (v: float32) : uint16 =
        if Single.IsNaN(v) then
            // Preserve NaN — keep sign + force exponent to all-ones + at
            // least one mantissa bit.
            0x7FC0us
        else
            let bits = BitConverter.SingleToUInt32Bits(v)
            // Round half to even.
            let lsb = (bits >>> 16) &&& 1u
            let rounded = bits + 0x7FFFu + lsb
            uint16 (rounded >>> 16)

    /// Convert BFloat16 → IEEE-754 float32. Lossless: just left-pad with
    /// 16 zero mantissa bits.
    static let bf16ToF32 (v: uint16) : float32 =
        BitConverter.UInt32BitsToSingle(uint32 v <<< 16)

    /// Append a single-precision float, converting to BFloat16 with
    /// round-to-nearest-even.
    member this.AppendFloat(v: float32) =
        this.Append(f32ToBF16 v)

    /// Read row i as float32. The conversion is lossless from the BFloat16
    /// side — the float32 result has 16 zero bits in its mantissa tail.
    member this.RowFloat(i: int) : float32 =
        bf16ToF32 (this.Row(i))
