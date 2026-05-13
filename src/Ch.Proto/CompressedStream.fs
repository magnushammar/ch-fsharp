namespace Ch.Proto

open System
open System.Buffers.Binary
open System.IO
open K4os.Compression.LZ4

/// Decompressing wrapper around another Stream.
/// Mirrors `compress.Reader` in ch-go (`compress/reader.go`).
///
/// Wire frame per block:
///   checksum (CityHash128, 16 B) | method (1 B) | rawSize u32 LE | dataSize u32 LE | payload[rawSize-9]
///
/// `rawSize` is the *compressed* size INCLUDING the 9-byte sub-header
/// (method + 2 sizes). `dataSize` is the *uncompressed* size. The checksum
/// covers `method + sub-header + payload` — i.e. the rawSize bytes.
///
/// Method codes (`compress/compress.go`):
///   0x82 — LZ4 / LZ4HC (same decode path)
///   0x90 — ZSTD (NotSupported in MVP)
///   0x02 — None (payload is the uncompressed data, still checksummed)
[<Sealed>]
type CompressedStream(inner: Stream) =
    inherit Stream()

    // Limits mirror ch-go's `compress.maxDataSize` / `maxBlockSize` (128 MB).
    [<Literal>]
    let MaxBlockSize = 128 * 1024 * 1024

    [<Literal>]
    let HeaderSize = 25 // 16 checksum + 1 method + 4 rawSize + 4 dataSize

    let header : byte array = Array.zeroCreate HeaderSize
    let mutable raw : byte array = Array.Empty<byte>()
    let mutable data : byte array = Array.Empty<byte>()
    let mutable dataLen = 0
    let mutable dataPos = 0

    let readFull (buf: Span<byte>) =
        let mutable remaining = buf.Length
        let mutable offset = 0
        while remaining > 0 do
            let n = inner.Read(buf.Slice(offset, remaining))
            if n <= 0 then
                raise (EndOfStreamException
                    $"EOF in compressed block: needed {buf.Length}, got {offset}")
            offset <- offset + n
            remaining <- remaining - n

    let readBlock () =
        readFull (header.AsSpan())

        let rawSize = int (BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan(header, 17, 4)))
        let dataSize = int (BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan(header, 21, 4)))
        let payloadSize = rawSize - 9     // payload-only size

        if payloadSize < 0 || payloadSize > MaxBlockSize then
            raise (InvalidDataException $"invalid compressed payload size {payloadSize}")
        if dataSize < 0 || dataSize > MaxBlockSize then
            raise (InvalidDataException $"invalid uncompressed size {dataSize}")

        // Layout: raw[0..16) = checksum, raw[16..25) = sub-header, raw[25..25+payloadSize) = payload.
        let needed = HeaderSize + payloadSize
        if raw.Length < needed then
            raw <- Array.zeroCreate (max needed (raw.Length * 2))
        Array.blit header 0 raw 0 HeaderSize
        if payloadSize > 0 then
            readFull (raw.AsSpan(HeaderSize, payloadSize))

        // Verify CityHash128 over [method..end] = `rawSize` bytes.
        let expectedLow = BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan(raw, 0, 8))
        let expectedHigh = BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan(raw, 8, 8))
        let computed = CityHash128.hash (ReadOnlySpan(raw, 16, 9 + payloadSize))
        if computed.Low <> expectedLow || computed.High <> expectedHigh then
            raise (InvalidDataException
                $"compressed block checksum mismatch (got {computed.Low:x16}{computed.High:x16}, expected {expectedLow:x16}{expectedHigh:x16})")

        if data.Length < dataSize then
            data <- Array.zeroCreate (max dataSize (data.Length * 2))
        dataLen <- dataSize
        dataPos <- 0

        let methodByte = raw.[16]
        match methodByte with
        | 0x82uy ->
            // LZ4 / LZ4HC. K4os.Compression.LZ4.LZ4Codec.Decode is the raw
            // LZ4 block API (no LZ4 frame headers) — exactly what
            // pierrec/lz4's UncompressBlock does in ch-go.
            if dataSize > 0 then
                let n =
                    LZ4Codec.Decode(
                        ReadOnlySpan(raw, HeaderSize, payloadSize),
                        Span(data, 0, dataSize))
                if n <> dataSize then
                    raise (InvalidDataException
                        $"LZ4 decompressed {n} bytes, expected {dataSize}")
        | 0x02uy ->
            // No compression, payload IS the data.
            if payloadSize <> dataSize then
                raise (InvalidDataException
                    $"method=None but payloadSize {payloadSize} != dataSize {dataSize}")
            if payloadSize > 0 then
                Array.blit raw HeaderSize data 0 payloadSize
        | 0x90uy ->
            raise (NotSupportedException "ZSTD decompression not implemented in MVP")
        | other ->
            raise (InvalidDataException $"unknown compression method 0x{other:x2}")

    member _.Inner = inner

    override _.Read(buffer: Span<byte>) : int =
        if dataPos >= dataLen then
            readBlock ()
        let toCopy = min buffer.Length (dataLen - dataPos)
        ReadOnlySpan(data, dataPos, toCopy).CopyTo(buffer)
        dataPos <- dataPos + toCopy
        toCopy

    override this.Read(buffer: byte array, offset: int, count: int) : int =
        this.Read(buffer.AsSpan(offset, count))

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())
    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())
    override _.Flush() = ()
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength(_) = raise (NotSupportedException())
    override _.Write(_: byte array, _: int, _: int) = raise (NotSupportedException())

/// Writer-side helpers for the same block-frame format. The MVP only needs to
/// emit the tiny end-of-data marker, so we never run LZ4 *encoding* — every
/// outgoing block we send uses method=None (still checksummed for the server's
/// integrity check).
[<RequireQualifiedAccess>]
module CompressedFrame =

    /// Append `payload` to `dest`, wrapped in a method=None compressed frame:
    ///   checksum (CityHash128, 16 B) | 0x02 | rawSize u32 LE | dataSize u32 LE | payload
    let wrapNone (dest: Buf) (payload: ReadOnlySpan<byte>) =
        let dataSize = payload.Length
        let total = 25 + dataSize
        let scratch : byte array = Array.zeroCreate total
        scratch.[16] <- 0x02uy
        BinaryPrimitives.WriteUInt32LittleEndian(scratch.AsSpan(17, 4), uint32 (9 + dataSize))
        BinaryPrimitives.WriteUInt32LittleEndian(scratch.AsSpan(21, 4), uint32 dataSize)
        if dataSize > 0 then
            payload.CopyTo(scratch.AsSpan(25, dataSize))
        let h = CityHash128.hash (ReadOnlySpan(scratch, 16, 9 + dataSize))
        BinaryPrimitives.WriteUInt64LittleEndian(scratch.AsSpan(0, 8), h.Low)
        BinaryPrimitives.WriteUInt64LittleEndian(scratch.AsSpan(8, 8), h.High)
        dest.PutRaw(ReadOnlySpan(scratch, 0, total))

    /// Append `payload` to `dest`, wrapped in a method=0x82 (LZ4) compressed
    /// frame. Same envelope as `wrapNone` but the payload bytes are LZ4-
    /// encoded (raw block API, no LZ4 frame magic — matches ch-go's
    /// `compress.Writer`). Falls back to `wrapNone` if LZ4 would inflate
    /// past the raw size (e.g. on tiny / random inputs).
    let wrapLZ4 (dest: Buf) (payload: ReadOnlySpan<byte>) =
        let dataSize = payload.Length
        // K4os' MaximumOutputSize gives the worst-case bound.
        let maxOut = LZ4Codec.MaximumOutputSize(dataSize)
        let compressed : byte array = Array.zeroCreate maxOut
        let n =
            if dataSize = 0 then 0
            else LZ4Codec.Encode(payload, Span(compressed, 0, maxOut))
        if n < 0 || n >= dataSize then
            // Compression didn't shrink it — emit raw with method=None.
            wrapNone dest payload
        else
            let payloadSize = n
            let total = 25 + payloadSize
            let scratch : byte array = Array.zeroCreate total
            scratch.[16] <- 0x82uy
            BinaryPrimitives.WriteUInt32LittleEndian(scratch.AsSpan(17, 4), uint32 (9 + payloadSize))
            BinaryPrimitives.WriteUInt32LittleEndian(scratch.AsSpan(21, 4), uint32 dataSize)
            Array.blit compressed 0 scratch 25 payloadSize
            let h = CityHash128.hash (ReadOnlySpan(scratch, 16, 9 + payloadSize))
            BinaryPrimitives.WriteUInt64LittleEndian(scratch.AsSpan(0, 8), h.Low)
            BinaryPrimitives.WriteUInt64LittleEndian(scratch.AsSpan(8, 8), h.High)
            dest.PutRaw(ReadOnlySpan(scratch, 0, total))
