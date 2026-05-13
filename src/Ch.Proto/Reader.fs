namespace Ch.Proto

open System
open System.Buffers.Binary
open System.IO
open System.Text

exception UnexpectedEndOfStreamException of string

/// Decode-side reader over a Stream. The stream should be a
/// `BufferedStream(NetworkStream, 128*1024)` — small primitives (uvarint,
/// strings, fixed-size ints) come from the buffer; bulk `ReadFull` for
/// >128 KB falls through directly to the socket.
///
/// The receive loop is synchronous: this matches ch-go's design and avoids
/// `ReadAsync` overhead on the inner loop.
[<Sealed>]
type Reader(rawStream: Stream) =
    let small : byte array = Array.zeroCreate 16
    let decompressed = new CompressedStream(rawStream) :> Stream
    let mutable active : Stream = rawStream

    /// Switch the active source to the decompressing stream. Use before
    /// reading a compressed block (Data / Totals / Extremes).
    member _.EnableCompression() = active <- decompressed

    /// Switch back to the raw underlying stream.
    member _.DisableCompression() = active <- rawStream

    member _.RawStream : Stream = rawStream

    member private _.ReadFullInto(span: Span<byte>) : unit =
        let mutable remaining = span.Length
        let mutable offset = 0
        while remaining > 0 do
            let n = active.Read(span.Slice(offset, remaining))
            if n <= 0 then
                raise (UnexpectedEndOfStreamException
                    $"expected {remaining} more bytes (got {offset} of {span.Length})")
            offset <- offset + n
            remaining <- remaining - n

    /// Read exactly span.Length bytes into the destination. Loops on short reads.
    /// This is the **hot path** for column body decoding.
    member this.ReadFull(span: Span<byte>) = this.ReadFullInto(span)

    member this.Byte() : byte =
        this.ReadFullInto(small.AsSpan(0, 1))
        small.[0]

    member this.Bool() : bool =
        match this.Byte() with
        | 0uy -> false
        | 1uy -> true
        | other -> raise (InvalidDataException $"bad bool value 0x{other:X2}")

    member this.Int32() : int32 =
        this.ReadFullInto(small.AsSpan(0, 4))
        BinaryPrimitives.ReadInt32LittleEndian(small.AsSpan(0, 4))

    member this.Int64() : int64 =
        this.ReadFullInto(small.AsSpan(0, 8))
        BinaryPrimitives.ReadInt64LittleEndian(small.AsSpan(0, 8))

    member this.UInt32() : uint32 =
        this.ReadFullInto(small.AsSpan(0, 4))
        BinaryPrimitives.ReadUInt32LittleEndian(small.AsSpan(0, 4))

    member this.UInt64() : uint64 =
        this.ReadFullInto(small.AsSpan(0, 8))
        BinaryPrimitives.ReadUInt64LittleEndian(small.AsSpan(0, 8))

    /// LEB128. Up to 10 bytes for a uint64. Throws on overflow.
    member this.UVarInt() : uint64 =
        let mutable result = 0UL
        let mutable shift = 0
        let mutable cont = true
        while cont do
            if shift >= 64 then
                raise (InvalidDataException "uvarint overflows uint64")
            let b = this.Byte()
            result <- result ||| ((uint64 (b &&& 0x7Fuy)) <<< shift)
            if b < 0x80uy then
                cont <- false
            else
                shift <- shift + 7
        result

    /// `proto/reader.go:152` — `Int()` is `int(UVarInt())`.
    member this.Int() : int = int (this.UVarInt())

    /// Read a length-prefixed UTF-8 string. Allocates.
    member this.Str() : string =
        let len = this.Int()
        if len = 0 then ""
        elif len < 0 then
            raise (InvalidDataException $"negative string length {len}")
        else
            let buf : byte array = Array.zeroCreate len
            this.ReadFullInto(buf.AsSpan())
            Encoding.UTF8.GetString(buf, 0, len)

    /// Skip exactly `n` bytes from the stream.
    member this.Skip(n: int) =
        let mutable remaining = n
        let buf : byte array = Array.zeroCreate (min n 4096)
        while remaining > 0 do
            let chunk = min remaining buf.Length
            this.ReadFullInto(buf.AsSpan(0, chunk))
            remaining <- remaining - chunk

    /// Read a ServerCode (single uvarint byte). Throws on unknown code.
    member this.ServerCode() : ServerCode =
        let n = this.UVarInt()
        if n > 255UL then
            raise (InvalidDataException $"server code {n} out of byte range")
        LanguagePrimitives.EnumOfValue<byte, ServerCode>(byte n)
