namespace Ch.Proto

open System
open System.Runtime.InteropServices

/// Polymorphic interface every column result must satisfy. The Client uses
/// this to look up + decode column data by name without knowing the concrete
/// 'T. Mirrors ch-go's `ColResult` interface (`proto/column.go`).
type IColumnResult =
    abstract Type : string
    abstract Rows : int
    abstract Reset : unit -> unit
    abstract DecodeColumn : Reader * int -> unit

/// Generic fixed-width column. Equivalent to ch-go's `Col<T>_unsafe_gen.go`:
/// the byte buffer IS the LE wire encoding of the row sequence, accessed via
/// `MemoryMarshal.Cast` for zero-copy reads/writes.
///
/// Subclassed (not used directly) so each concrete column reports its own
/// `ColumnType` string. `'T` must be `unmanaged` (a blittable value type).
[<AbstractClass>]
type ColPrimitive<'T
        when 'T : unmanaged
        and 'T : struct
        and 'T : (new : unit -> 'T)
        and 'T :> ValueType>(typeName: string) =
    let mutable buf : byte array = Array.Empty<byte>()
    let mutable count : int = 0
    let elemSize = sizeof<'T>

    let ensure (needed: int) =
        if buf.Length < needed then
            let newCap = max needed (max 64 (buf.Length * 2))
            Array.Resize(&buf, newCap)  // preserves existing bytes

    /// ClickHouse type string (e.g. "Int32"). Wire-significant.
    member _.Type : string = typeName

    /// Number of rows currently held.
    member _.Rows : int = count

    /// Reset to zero rows. Capacity preserved.
    member _.Reset() = count <- 0

    /// Append a single value.
    member _.Append(v: 'T) =
        let needed = (count + 1) * elemSize
        ensure needed
        let mutable vv = v
        MemoryMarshal.Write<'T>(buf.AsSpan(count * elemSize, elemSize), &vv)
        count <- count + 1

    /// Append a span of values.
    member _.AppendRange(vs: ReadOnlySpan<'T>) =
        if vs.Length > 0 then
            let needed = (count + vs.Length) * elemSize
            ensure needed
            let dst = MemoryMarshal.Cast<byte, 'T>(buf.AsSpan(count * elemSize, vs.Length * elemSize))
            vs.CopyTo(dst)
            count <- count + vs.Length

    /// Read row i. No bounds check beyond what the underlying span provides.
    member _.Row(i: int) : 'T =
        let slice = ReadOnlySpan(buf, i * elemSize, elemSize)
        MemoryMarshal.Read<'T>(slice)

    /// Zero-copy view of the current rows as a span of `'T`.
    member _.AsSpan() : ReadOnlySpan<'T> =
        MemoryMarshal.Cast<byte, 'T>(ReadOnlySpan(buf, 0, count * elemSize))

    /// Underlying byte buffer (for tests / bench inspection).
    member _.RawBuffer : byte array = buf

    /// Element size in bytes.
    member _.ElemSize : int = elemSize

    /// Decode `n` rows from the reader. **Single** `ReadFull` — this is the
    /// bench-critical path.
    member _.DecodeColumn(r: Reader, n: int) =
        let needed = n * elemSize
        ensure needed
        if needed > 0 then
            r.ReadFull(buf.AsSpan(0, needed))
        count <- n

    /// Append the current rows to the buffer (LE wire encoding).
    member _.EncodeColumn(b: Buf) =
        if count > 0 then
            b.PutRaw(ReadOnlySpan(buf, 0, count * elemSize))

    interface IColumnResult with
        member this.Type = this.Type
        member this.Rows = this.Rows
        member this.Reset() = this.Reset()
        member this.DecodeColumn(r, n) = this.DecodeColumn(r, n)


[<Sealed>] type ColInt8()    = inherit ColPrimitive<int8>("Int8")
[<Sealed>] type ColInt16()   = inherit ColPrimitive<int16>("Int16")
[<Sealed>] type ColInt32()   = inherit ColPrimitive<int32>("Int32")
[<Sealed>] type ColInt64()   = inherit ColPrimitive<int64>("Int64")
[<Sealed>] type ColUInt8()   = inherit ColPrimitive<uint8>("UInt8")
[<Sealed>] type ColUInt16()  = inherit ColPrimitive<uint16>("UInt16")
[<Sealed>] type ColUInt32()  = inherit ColPrimitive<uint32>("UInt32")
[<Sealed>] type ColUInt64()  = inherit ColPrimitive<uint64>("UInt64")
[<Sealed>] type ColFloat32() = inherit ColPrimitive<float32>("Float32")
[<Sealed>] type ColFloat64() = inherit ColPrimitive<float>("Float64")
[<Sealed>] type ColBool()    = inherit ColPrimitive<bool>("Bool")
