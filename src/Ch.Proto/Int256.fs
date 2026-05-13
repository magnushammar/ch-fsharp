namespace Ch.Proto

open System
open System.Runtime.InteropServices

/// 256-bit signed integer — 32 bytes on the wire (LE).
/// Layout: two `System.UInt128` halves, low first. ClickHouse stores
/// `Int256` / `UInt256` and `Decimal256` with this layout. .NET 10 has no
/// native 256-bit type — this struct fills the gap with the right
/// `Sequential` layout so `MemoryMarshal.Read/Write<Int256>` works on a
/// raw byte buffer.
///
/// Arithmetic is intentionally absent — this is a wire-level value
/// carrier. Users who need to compute on it can decompose via `.Low` and
/// `.High` (each a `UInt128`) and reassemble.
[<Struct; StructLayout(LayoutKind.Sequential)>]
type Int256 =
    val Low: UInt128
    val High: UInt128

    new (low: UInt128, high: UInt128) = { Low = low; High = high }
    new (low: uint64) = Int256(UInt128(0UL, low), UInt128.Zero)

    static member Zero = Int256(UInt128.Zero, UInt128.Zero)

    override this.ToString() = $"Int256({this.Low}, {this.High})"


/// 256-bit unsigned integer — same wire layout as `Int256`. Kept as a
/// distinct struct so columns stay type-safe (`ColInt256` vs
/// `ColUInt256`). Wire bytes are identical; interpretation is the
/// caller's concern.
[<Struct; StructLayout(LayoutKind.Sequential)>]
type UInt256 =
    val Low: UInt128
    val High: UInt128

    new (low: UInt128, high: UInt128) = { Low = low; High = high }
    new (low: uint64) = UInt256(UInt128(0UL, low), UInt128.Zero)

    static member Zero = UInt256(UInt128.Zero, UInt128.Zero)

    override this.ToString() = $"UInt256({this.Low}, {this.High})"
