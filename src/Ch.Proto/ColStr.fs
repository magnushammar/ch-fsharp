namespace Ch.Proto

open System
open System.IO
open System.Text

/// Variable-length String column. Mirrors ch-go's `ColStr` (`proto/col_str.go`):
/// all string bytes live in one contiguous `data` buffer; `ends.[i]` is the
/// END index of row i in `data` (row 0 starts at 0, row i starts at ends.[i-1]).
///
/// Wire encoding per row: uvarint(byteLen) + UTF-8 bytes. Decode is bounded by
/// the row count the caller supplies.
[<Sealed>]
type ColStr() =
    let mutable data : byte array = Array.Empty<byte>()
    let mutable dataLen : int = 0
    let mutable ends : int array = Array.Empty<int>()
    let mutable count : int = 0

    let ensureData (needed: int) =
        if data.Length < needed then
            let newCap = max needed (max 64 (data.Length * 2))
            Array.Resize(&data, newCap)

    let ensureEnds (needed: int) =
        if ends.Length < needed then
            let newCap = max needed (max 16 (ends.Length * 2))
            Array.Resize(&ends, newCap)

    member _.Type = "String"
    member _.Rows = count
    member _.DataLength = dataLen

    member _.Reset() =
        dataLen <- 0
        count <- 0

    /// Append a UTF-8 byte slice as one row.
    member _.AppendBytes(s: ReadOnlySpan<byte>) =
        ensureData (dataLen + s.Length)
        ensureEnds (count + 1)
        if s.Length > 0 then
            s.CopyTo(data.AsSpan(dataLen, s.Length))
        dataLen <- dataLen + s.Length
        ends.[count] <- dataLen
        count <- count + 1

    /// Append a string (encoded to UTF-8).
    member this.Append(s: string) =
        if String.IsNullOrEmpty s then
            ensureEnds (count + 1)
            ends.[count] <- dataLen
            count <- count + 1
        else
            let byteLen = Encoding.UTF8.GetByteCount(s)
            ensureData (dataLen + byteLen)
            ensureEnds (count + 1)
            let written = Encoding.UTF8.GetBytes(s.AsSpan(), data.AsSpan(dataLen, byteLen))
            dataLen <- dataLen + written
            ends.[count] <- dataLen
            count <- count + 1

    /// Zero-copy byte view of row i.
    member _.RowSpan(i: int) : ReadOnlySpan<byte> =
        let startIdx = if i = 0 then 0 else ends.[i - 1]
        ReadOnlySpan(data, startIdx, ends.[i] - startIdx)

    /// UTF-8 decode row i to a managed string.
    member this.Row(i: int) : string =
        Encoding.UTF8.GetString(this.RowSpan(i))

    member _.RawData : byte array = data
    member _.RowEnds : int array = ends

    /// Encode `count` rows: each is `uvarint(byteLen) + bytes`.
    member _.EncodeColumn(b: Buf) =
        let mutable prevEnd = 0
        for i in 0 .. count - 1 do
            let endIdx = ends.[i]
            let len = endIdx - prevEnd
            b.PutUVarInt(uint64 len)
            if len > 0 then
                b.PutRaw(ReadOnlySpan(data, prevEnd, len))
            prevEnd <- endIdx

    /// Decode `n` rows. Mirrors ch-go's adaptive batch alloc heuristic: when
    /// the first row is short (<128 B), preallocate `len * remaining` bytes
    /// in one shot, betting subsequent rows are similarly sized.
    member _.DecodeColumn(r: Reader, n: int) =
        ensureEnds n
        let mutable curEnd = 0
        for i in 0 .. n - 1 do
            let len = r.Int()
            if len < 0 then
                raise (InvalidDataException $"negative string length {len}")
            // Adaptive grow: for short strings, allocate enough for the rest.
            let newEnd = curEnd + len
            if data.Length < newEnd then
                let batchHint =
                    if len < 128 then len * (n - i)
                    else len
                let target = max newEnd (curEnd + batchHint)
                ensureData target
            if len > 0 then
                r.ReadFull(data.AsSpan(curEnd, len))
            curEnd <- newEnd
            ends.[i] <- curEnd
        dataLen <- curEnd
        count <- n

    interface IColumnResult with
        member this.Type = this.Type
        member this.Rows = this.Rows
        member this.Reset() = this.Reset()
        member this.DecodeColumn(r, n) = this.DecodeColumn(r, n)
