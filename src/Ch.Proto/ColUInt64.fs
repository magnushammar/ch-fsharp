namespace Ch.Proto

open System
open System.Buffers.Binary
open System.Runtime.InteropServices

/// `UInt64` column. The MVP's one and only typed column.
///
/// Equivalent to ch-go's `ColUInt64` (`proto/col_uint64_unsafe_gen.go`). On a
/// little-endian host the byte representation of a `uint64[]` IS the wire
/// representation — so we read straight into a `byte[]` of `rows * 8` bytes
/// and reinterpret it as `Span<uint64>` for the user.
///
/// Hot-path invariants:
///   • `DecodeColumn` issues **one** `ReadFull` per block — no per-row loop.
///   • `buf` is reused across blocks; only grows when a block exceeds the
///     current high-water mark.
///   • `Row(i)` and `AsSpan()` are zero-copy views into the byte buffer.
///   • No virtual dispatch — sealed class, consumed by concrete type.
[<Sealed>]
type ColUInt64() =
    let mutable buf : byte array = Array.Empty<byte>()
    let mutable rows : int = 0

    /// Underlying byte buffer (LE-encoded uint64 sequence). Exposed for tests
    /// and benchmarks; do not mutate length externally.
    member _.RawBuffer = buf

    member _.Rows = rows

    /// Reset row count to zero. Capacity is preserved across queries.
    member _.Reset() = rows <- 0

    /// Decode `n` rows of UInt64 from `r` into our buffer. **Single** ReadFull
    /// — this is the bench-critical path.
    member _.DecodeColumn(r: Reader, n: int) =
        let needed = n * 8
        if buf.Length < needed then
            // Geometric growth, but never less than `needed`.
            let newCap = max needed (max 1024 (buf.Length * 2))
            buf <- Array.zeroCreate newCap
        if needed > 0 then
            r.ReadFull(buf.AsSpan(0, needed))
        rows <- n

    /// Read the i-th row (0 <= i < Rows).
    member _.Row(i: int) : uint64 =
        BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan(buf, i * 8, 8))

    /// Zero-copy view into the current rows as a span of `uint64`. The span is
    /// only valid until the next `DecodeColumn` call that grows `buf`.
    ///
    /// On LE hosts this is the raw byte buffer reinterpreted — no copy, no
    /// allocation. Mirrors ch-go's unsafe.Slice trick (`col_uint64_unsafe_gen.go`).
    member _.AsSpan() : ReadOnlySpan<uint64> =
        MemoryMarshal.Cast<byte, uint64>(ReadOnlySpan(buf, 0, rows * 8))
