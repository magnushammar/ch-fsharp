namespace Ch.Proto

open System
open System.Buffers.Binary

/// Shared backing for "fixed N bytes per row" columns (UUID, FixedString(N),
/// IPv6). Subclasses override EncodeColumn / DecodeColumn when the wire bytes
/// need transforming (e.g. UUID's byte-swapped halves).
[<AbstractClass>]
type ColFixedBytes(typeName: string, elemSize: int) =
    let mutable buf : byte array = Array.Empty<byte>()
    let mutable count : int = 0

    do
        if elemSize <= 0 then
            raise (ArgumentOutOfRangeException("elemSize", "must be positive"))

    let ensure (needed: int) =
        if buf.Length < needed then
            let newCap = max needed (max 64 (buf.Length * 2))
            Array.Resize(&buf, newCap)

    member _.Type = typeName
    member _.Rows = count
    member _.ElemSize = elemSize
    member _.RawBuffer = buf

    member _.Reset() = count <- 0

    /// Append one row's bytes. Must be exactly ElemSize.
    member _.AppendBytes(src: ReadOnlySpan<byte>) =
        if src.Length <> elemSize then
            raise (ArgumentException($"expected {elemSize} bytes, got {src.Length}", "src"))
        ensure ((count + 1) * elemSize)
        src.CopyTo(buf.AsSpan(count * elemSize, elemSize))
        count <- count + 1

    /// Zero-copy view of row i. Mind it is the *in-memory* representation;
    /// subclasses may apply transformation on encode/decode boundaries.
    member _.RowSpan(i: int) : ReadOnlySpan<byte> =
        ReadOnlySpan(buf, i * elemSize, elemSize)

    /// Read N rows of raw bytes. Subclasses override to apply transforms.
    abstract DecodeColumn : Reader * int -> unit
    default _.DecodeColumn(r: Reader, n: int) =
        let needed = n * elemSize
        ensure needed
        if needed > 0 then
            r.ReadFull(buf.AsSpan(0, needed))
        count <- n

    /// Write `count` rows of raw bytes. Subclasses override to apply transforms.
    abstract EncodeColumn : Buf -> unit
    default _.EncodeColumn(b: Buf) =
        if count > 0 then
            b.PutRaw(ReadOnlySpan(buf, 0, count * elemSize))

    interface IColumnResult with
        member this.Type = this.Type
        member this.Rows = this.Rows
        member this.Reset() = this.Reset()
        member this.DecodeColumn(r, n) = this.DecodeColumn(r, n)
        member this.EncodeColumn(b) = this.EncodeColumn(b)


/// `FixedString(N)` — exactly N bytes per row, no length prefix on the wire.
[<Sealed>]
type ColFixedStr(size: int) =
    inherit ColFixedBytes(sprintf "FixedString(%d)" size, size)


/// `UUID` — 16 bytes per row. ClickHouse stores UUIDs as two big-endian
/// UInt64 halves; on the wire those halves are byte-reversed to LE. We hold
/// bytes in ch-go-style "in-memory" RFC-4122 layout (BE halves) and apply
/// the swap only on encode/decode.
[<Sealed>]
type ColUUID() =
    inherit ColFixedBytes("UUID", 16)

    static let bswapHalves (span: Span<byte>) =
        let mutable i = 0
        while i + 16 <= span.Length do
            let lo = BinaryPrimitives.ReadUInt64BigEndian(span.Slice(i, 8))
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(i, 8), lo)
            let hi = BinaryPrimitives.ReadUInt64BigEndian(span.Slice(i + 8, 8))
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(i + 8, 8), hi)
            i <- i + 16

    override this.EncodeColumn(b: Buf) =
        if this.Rows > 0 then
            let total = this.Rows * 16
            let scratch : byte array = Array.zeroCreate total
            Array.blit this.RawBuffer 0 scratch 0 total
            bswapHalves (scratch.AsSpan(0, total))
            b.PutRaw(ReadOnlySpan(scratch, 0, total))

    override this.DecodeColumn(r: Reader, n: int) =
        base.DecodeColumn(r, n)
        if n > 0 then
            bswapHalves (this.RawBuffer.AsSpan(0, n * 16))


/// `IPv6` — 16 bytes per row in network byte order. No transform.
[<Sealed>]
type ColIPv6() =
    inherit ColFixedBytes("IPv6", 16)

    member this.AppendIP(addr: System.Net.IPAddress) =
        let bytes = addr.GetAddressBytes()
        if bytes.Length <> 16 then
            raise (ArgumentException("not an IPv6 address", "addr"))
        this.AppendBytes(ReadOnlySpan(bytes))

    member this.RowIP(i: int) : System.Net.IPAddress =
        let span = this.RowSpan(i)
        System.Net.IPAddress(span.ToArray())
