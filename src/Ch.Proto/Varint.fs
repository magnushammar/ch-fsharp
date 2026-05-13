namespace Ch.Proto

open System

/// LEB128 unsigned varint. ClickHouse uses this for all uvarint encodings
/// (lengths, codes, settings flags). Max 10 bytes for a uint64.
[<RequireQualifiedAccess>]
module Varint =

    /// Maximum bytes a uint64 uvarint can occupy.
    [<Literal>]
    let MaxLen = 10

    /// Write `value` into `span` starting at index 0. Returns bytes written.
    /// `span` must have capacity >= 10.
    let inline write (span: Span<byte>) (value: uint64) : int =
        let mutable v = value
        let mutable i = 0
        while v >= 0x80UL do
            span.[i] <- byte (v ||| 0x80UL)
            v <- v >>> 7
            i <- i + 1
        span.[i] <- byte v
        i + 1

    /// Try to read a uvarint from `span`. Returns Some (value, bytesConsumed) on
    /// success; None if the span is too short or the encoding overflows uint64.
    let tryRead (span: ReadOnlySpan<byte>) : struct (uint64 * int) voption =
        let mutable result = 0UL
        let mutable shift = 0
        let mutable i = 0
        let mutable ok = false
        let mutable stop = false
        while not stop && i < span.Length do
            let b = span.[i]
            if shift >= 64 then
                stop <- true
            else
                result <- result ||| ((uint64 (b &&& 0x7Fuy)) <<< shift)
                i <- i + 1
                if b < 0x80uy then
                    ok <- true
                    stop <- true
                else
                    shift <- shift + 7
        if ok then ValueSome (struct (result, i)) else ValueNone
