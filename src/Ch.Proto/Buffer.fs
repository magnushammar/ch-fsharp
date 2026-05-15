namespace Ch.Proto

open System
open System.Buffers
open System.Buffers.Binary
open System.IO
open System.Text

/// Encode-side buffer. Wraps an ArrayBufferWriter<byte> so growth is amortized
/// and `WrittenMemory` can be handed straight to `Stream.WriteAsync`.
///
/// The encode path for a SELECT is < 1 KB total per query, so we don't bother
/// with vectored writes or pooling. Reuse this buffer across queries by calling
/// `Reset()` — capacity is preserved.
///
/// Named `Buf` to avoid collision with `System.Buffer`.
[<Sealed>]
type Buf(initialCapacity: int) =
    let writer = ArrayBufferWriter<byte>(initialCapacity)

    new() = Buf(256)

    member _.Length = writer.WrittenCount
    member _.WrittenSpan : ReadOnlySpan<byte> = writer.WrittenSpan
    member _.WrittenMemory : ReadOnlyMemory<byte> = writer.WrittenMemory
    member _.Reset() = writer.ResetWrittenCount()

    member _.PutByte(b: byte) =
        let span = writer.GetSpan(1)
        span.[0] <- b
        writer.Advance(1)

    member this.PutBool(v: bool) =
        this.PutByte(if v then 1uy else 0uy)

    member _.PutInt32(v: int32) =
        let span = writer.GetSpan(4)
        BinaryPrimitives.WriteInt32LittleEndian(span, v)
        writer.Advance(4)

    member _.PutInt64(v: int64) =
        let span = writer.GetSpan(8)
        BinaryPrimitives.WriteInt64LittleEndian(span, v)
        writer.Advance(8)

    member _.PutUInt32(v: uint32) =
        let span = writer.GetSpan(4)
        BinaryPrimitives.WriteUInt32LittleEndian(span, v)
        writer.Advance(4)

    member _.PutUInt64(v: uint64) =
        let span = writer.GetSpan(8)
        BinaryPrimitives.WriteUInt64LittleEndian(span, v)
        writer.Advance(8)

    member _.PutUVarInt(x: uint64) =
        let span = writer.GetSpan(Varint.MaxLen)
        let n = Varint.write span x
        writer.Advance(n)

    /// `proto/buffer.go:80` — `PutInt(x int)` is `PutUVarInt(uint64(x))`, NOT
    /// fixed-width. Used for: ClientHello major/minor/protocolVersion, Block
    /// columns/rows, Stage, Compression, ClientInfo Major/Minor/ProtocolVersion.
    member this.PutInt(x: int) = this.PutUVarInt(uint64 x)

    member _.PutRaw(src: ReadOnlySpan<byte>) =
        if src.Length > 0 then
            let dst = writer.GetSpan(src.Length)
            src.CopyTo(dst)
            writer.Advance(src.Length)

    member this.PutString(s: string) =
        if String.IsNullOrEmpty s then
            this.PutByte(0uy)  // uvarint 0
        else
            let byteLen = Encoding.UTF8.GetByteCount(s)
            this.PutUVarInt(uint64 byteLen)
            let dst = writer.GetSpan(byteLen)
            let written = Encoding.UTF8.GetBytes(s.AsSpan(), dst)
            writer.Advance(written)

    /// Encode a ClientCode (single byte).
    member this.PutClientCode(c: ClientCode) = this.PutByte(byte c)

    /// Synchronous send + reset. Used by the query path so the receive loop
    /// runs inline on the caller's thread instead of being resumed on a
    /// threadpool worker — the latter triggers thread-injection + spin when
    /// the loop blocks in `read(2)`.
    member this.WriteToAndReset(stream: Stream) =
        stream.Write(writer.WrittenSpan)
        this.Reset()
