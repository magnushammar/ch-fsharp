namespace Ch.Proto

open System
open System.Buffers.Binary
open System.IO
open System.Text

exception UnexpectedEndOfStreamException of string

/// Decode-side reader over a Stream.
///
/// The Reader owns its read buffer: small primitives (uvarint, fixed-size
/// ints, short strings) are served straight out of `buf` by index — no
/// virtual `Stream.Read` per byte. `ReadFull` of a large span drains the
/// buffer then reads directly from the underlying stream.
///
/// `buf` always holds *raw* stream bytes. When compression is enabled the
/// high-level reads (`Byte`, `Int32`, `Str`, `ReadFull`, ...) route through
/// a `CompressedStream`, which itself pulls raw bytes back through this
/// Reader's `DrainAndRead` (the `IRawByteSource` impl) — so the plain and
/// compressed paths share one underlying byte stream, and a read-ahead past
/// a temp-table name into the first compressed frame stays consistent.
///
/// The receive loop is synchronous: this matches ch-go's design and avoids
/// `ReadAsync` overhead on the inner loop.
[<Sealed>]
type Reader(source: Stream) =
    // 8 KB matches the old BufferedStream size: big enough that every block
    // header sits contiguously, small enough that the drain-copy a column
    // body pays on the buffer boundary stays cheap.
    let buf : byte array = Array.zeroCreate 8192
    let small : byte array = Array.zeroCreate 16
    let skipScratch : byte array = Array.zeroCreate 4096
    let mutable pos = 0
    let mutable len = 0
    let mutable compressed = false
    let mutable decompressed : CompressedStream option = None

    /// Refill `buf` from the underlying stream. Leaves `len = 0` at EOF.
    member private _.Fill() =
        pos <- 0
        len <- source.Read(buf.AsSpan())

    /// Read exactly `span.Length` raw bytes: drain whatever is buffered,
    /// then read the remainder straight from the underlying stream. This is
    /// the raw primitive — it ignores the `compressed` flag (it is what
    /// `CompressedStream` pulls its frame bytes through).
    member private _.DrainAndRead(span: Span<byte>) : unit =
        let mutable dst = 0
        let buffered = len - pos
        if buffered > 0 then
            let take = min buffered span.Length
            ReadOnlySpan(buf, pos, take).CopyTo(span)
            pos <- pos + take
            dst <- take
        while dst < span.Length do
            let n = source.Read(span.Slice(dst))
            if n <= 0 then
                raise (UnexpectedEndOfStreamException
                    $"expected {span.Length - dst} more bytes (got {dst} of {span.Length})")
            dst <- dst + n

    /// Read exactly `span.Length` bytes, honouring compression: when a
    /// compressed block is active the bytes come (decompressed) from the
    /// `CompressedStream`; otherwise straight off the raw stream.
    member private this.ReadInto(span: Span<byte>) : unit =
        if compressed then
            let cs = Option.get decompressed
            let mutable dst = 0
            while dst < span.Length do
                let n = cs.Read(span.Slice(dst))
                if n <= 0 then
                    raise (UnexpectedEndOfStreamException
                        $"expected {span.Length - dst} more bytes in compressed block (got {dst} of {span.Length})")
                dst <- dst + n
        else
            this.DrainAndRead(span)

    /// Switch to the decompressing path. Use before reading a compressed
    /// block (Data / Totals / Extremes). The `CompressedStream` is created
    /// once and reused — it carries the LZ4 scratch buffers across blocks.
    member this.EnableCompression() =
        match decompressed with
        | None -> decompressed <- Some(new CompressedStream(this :> IRawByteSource))
        | Some _ -> ()
        compressed <- true

    /// Switch back to the raw stream. Safe because a compressed block decode
    /// consumes its frame(s) exactly — no decompressed bytes are left over,
    /// and `buf` is positioned right after the last frame.
    member _.DisableCompression() = compressed <- false

    /// Read exactly span.Length bytes into the destination. This is the
    /// **hot path** for column body decoding.
    member this.ReadFull(span: Span<byte>) = this.ReadInto(span)

    member this.Byte() : byte =
        if not compressed && pos < len then
            let b = buf.[pos]
            pos <- pos + 1
            b
        else
            this.ByteSlow()

    member private this.ByteSlow() : byte =
        if compressed then
            this.ReadInto(small.AsSpan(0, 1))
            small.[0]
        else
            this.Fill()
            if len = 0 then
                raise (UnexpectedEndOfStreamException "expected 1 more byte (got EOF)")
            pos <- 1
            buf.[0]

    member this.Bool() : bool =
        match this.Byte() with
        | 0uy -> false
        | 1uy -> true
        | other -> raise (InvalidDataException $"bad bool value 0x{other:X2}")

    member this.Int32() : int32 =
        if not compressed && pos + 4 <= len then
            let v = BinaryPrimitives.ReadInt32LittleEndian(ReadOnlySpan(buf, pos, 4))
            pos <- pos + 4
            v
        else
            this.ReadInto(small.AsSpan(0, 4))
            BinaryPrimitives.ReadInt32LittleEndian(ReadOnlySpan(small, 0, 4))

    member this.Int64() : int64 =
        if not compressed && pos + 8 <= len then
            let v = BinaryPrimitives.ReadInt64LittleEndian(ReadOnlySpan(buf, pos, 8))
            pos <- pos + 8
            v
        else
            this.ReadInto(small.AsSpan(0, 8))
            BinaryPrimitives.ReadInt64LittleEndian(ReadOnlySpan(small, 0, 8))

    member this.UInt32() : uint32 =
        if not compressed && pos + 4 <= len then
            let v = BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan(buf, pos, 4))
            pos <- pos + 4
            v
        else
            this.ReadInto(small.AsSpan(0, 4))
            BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan(small, 0, 4))

    member this.UInt64() : uint64 =
        if not compressed && pos + 8 <= len then
            let v = BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan(buf, pos, 8))
            pos <- pos + 8
            v
        else
            this.ReadInto(small.AsSpan(0, 8))
            BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan(small, 0, 8))

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

    /// Read a length-prefixed UTF-8 string. Allocates the result string; on
    /// the uncompressed fast path the bytes are decoded straight out of
    /// `buf` with no intermediate byte[].
    member this.Str() : string =
        let n = this.Int()
        if n = 0 then ""
        elif n < 0 then
            raise (InvalidDataException $"negative string length {n}")
        elif not compressed && pos + n <= len then
            let s = Encoding.UTF8.GetString(buf, pos, n)
            pos <- pos + n
            s
        else
            let tmp : byte array = Array.zeroCreate n
            this.ReadInto(tmp.AsSpan())
            Encoding.UTF8.GetString(tmp, 0, n)

    /// Skip a length-prefixed string without materialising it. Used on the
    /// fast block-decode path, where the column name/type were already
    /// validated on the first block and re-reading them as `string` would
    /// just churn the GC.
    member this.SkipStr() =
        let n = this.Int()
        if n < 0 then
            raise (InvalidDataException $"negative string length {n}")
        elif n > 0 then
            this.Skip(n)

    /// Skip exactly `n` bytes from the (possibly decompressed) stream.
    member this.Skip(n: int) =
        if not compressed && pos + n <= len then
            pos <- pos + n
        else
            let mutable remaining = n
            while remaining > 0 do
                let chunk = min remaining skipScratch.Length
                this.ReadInto(skipScratch.AsSpan(0, chunk))
                remaining <- remaining - chunk

    /// Read a ServerCode (single uvarint byte). Throws on unknown code.
    member this.ServerCode() : ServerCode =
        let n = this.UVarInt()
        if n > 255UL then
            raise (InvalidDataException $"server code {n} out of byte range")
        LanguagePrimitives.EnumOfValue<byte, ServerCode>(byte n)

    interface IRawByteSource with
        member this.ReadRawFull(span) = this.DrainAndRead(span)
