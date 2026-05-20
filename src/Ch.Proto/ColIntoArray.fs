namespace Ch.Proto

open System
open System.Runtime.InteropServices

/// Pre-sized drain column: decodes server-sent rows **directly into a
/// caller-owned typed array**, advancing an internal offset.
///
/// Contrast with `ColPrimitive<'T>`, which owns its own byte buffer and
/// overwrites it on every block (streaming kernel shape). `ColIntoArray`
/// is the "Tier 2" drain shape:
///
///   - The caller allocates `dst : 'T[]` (typically sized by a prior
///     `SELECT count()` query).
///   - `DecodeColumn` writes block bytes straight into the back of
///     `dst` via `MemoryMarshal.AsBytes(Span(dst, offset, n))`.
///   - `Rows` returns the live offset, so a concurrent reader can use
///     `col.Rows` as a published frontier.
///   - `AsSpan()` returns exactly the decoded prefix
///     `ReadOnlySpan(dst, 0, offset)`.
///   - `Array` exposes the underlying typed array directly — random
///     access by index from any thread.
///
/// SELECT-only. `EncodeColumn` throws — for INSERT use `ColPrimitive`
/// or its sealed leaves.
///
/// `'T` is constrained the same way `ColPrimitive`'s element is —
/// blittable value type — so `MemoryMarshal.AsBytes` is a sound
/// zero-copy reinterpret. The ClickHouse type string is derived once
/// at construction from `typeof<'T>`.
type ColIntoArray<'T
        when 'T : unmanaged
        and 'T : struct
        and 'T : (new : unit -> 'T)
        and 'T :> ValueType
        and 'T : equality
        and 'T : not null>(dst: 'T[]) =

    let typeName : string =
        if typeof<'T> = typeof<int8>    then "Int8"
        elif typeof<'T> = typeof<int16>   then "Int16"
        elif typeof<'T> = typeof<int32>   then "Int32"
        elif typeof<'T> = typeof<int64>   then "Int64"
        elif typeof<'T> = typeof<uint8>   then "UInt8"
        elif typeof<'T> = typeof<uint16>  then "UInt16"
        elif typeof<'T> = typeof<uint32>  then "UInt32"
        elif typeof<'T> = typeof<uint64>  then "UInt64"
        elif typeof<'T> = typeof<float32> then "Float32"
        elif typeof<'T> = typeof<float>   then "Float64"
        elif typeof<'T> = typeof<bool>    then "Bool"
        else
            invalidArg "T"
                (sprintf "ColIntoArray<'T>: no ClickHouse type mapping for %s. \
                          Supported: Int8/16/32/64, UInt8/16/32/64, Float32/64, Bool."
                          (typeof<'T>.FullName))

    let elemSize = sizeof<'T>
    let mutable offset = 0

    /// ClickHouse type string (e.g. "Int64"). Wire-significant; derived
    /// from `typeof<'T>` at construction.
    member _.Type : string = typeName

    /// Live count of decoded rows. Equals the number of slots in `dst`
    /// that have been written to so far. Safe to read from a different
    /// thread than the one running `DecodeColumn` provided the writer
    /// publishes via a memory barrier (`Volatile.Write` against this
    /// value's last update).
    member _.Rows : int = offset

    /// Reset the offset to 0. The underlying `dst` array is **not**
    /// zeroed — subsequent decodes will overwrite slots 0..n-1 as the
    /// new blocks arrive.
    member _.Reset() = offset <- 0

    /// Zero-copy view of the decoded prefix: `ReadOnlySpan(dst, 0, Rows)`.
    /// Unlike `ColPrimitive.AsSpan()`, this span remains valid across
    /// subsequent `DecodeColumn` calls — `dst` is caller-owned and the
    /// column never resizes it. Newly-decoded rows extend beyond
    /// `span.Length`; re-acquire to see them.
    member _.AsSpan() : ReadOnlySpan<'T> =
        ReadOnlySpan(dst, 0, offset)

    /// Direct reference to the caller-owned destination array. Useful
    /// when the consumer wants ordinary `arr.[i]` indexing without
    /// going through `AsSpan()`. The array is the same object the
    /// caller passed at construction.
    member _.Array : 'T[] = dst

    /// Element size in bytes (same as `sizeof<'T>`).
    member _.ElemSize : int = elemSize

    /// Decode `n` rows **appended** to the current offset. Throws
    /// `InvalidOperationException` if `offset + n` would exceed
    /// `dst.Length`. The caller is responsible for sizing `dst`
    /// correctly (typically via a prior `SELECT count()` query).
    member _.DecodeColumn(r: Reader, n: int) =
        if n = 0 then () else
        if offset + n > dst.Length then
            raise (InvalidOperationException
                (sprintf "ColIntoArray<%s>: decode would overflow destination array \
                          (offset=%d + n=%d > dst.Length=%d). \
                          Size the array via a prior count() query, or use ColPrimitive \
                          for the streaming-overwrite shape."
                          (typeof<'T>.Name) offset n dst.Length))
        let bytes = MemoryMarshal.AsBytes(Span<'T>(dst, offset, n))
        r.ReadFull(bytes)
        offset <- offset + n

    /// SELECT-only column. INSERT use is a programming error.
    member _.EncodeColumn(_: Buf) =
        raise (NotSupportedException
            "ColIntoArray is a SELECT-only drain target. \
             For INSERT input use ColPrimitive or its sealed leaves \
             (ColInt64, ColFloat64, …).")

    interface IColumnResult with
        member this.Type = this.Type
        member this.Rows = this.Rows
        member this.Reset() = this.Reset()
        member this.DecodeColumn(r, n) = this.DecodeColumn(r, n)
        member this.EncodeColumn(b) = this.EncodeColumn(b)
