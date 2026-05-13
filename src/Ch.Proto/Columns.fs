namespace Ch.Proto

open System
open System.Runtime.InteropServices
open System.Text.RegularExpressions

/// Server-sent vs client-declared column type strings don't always agree
/// verbatim: ClickHouse sends `Decimal(P, S)` where our explicit-width
/// columns advertise `Decimal32`/`64`/`128`/`256`. ch-go reference:
/// `proto/column.go: decimalDowncast` + `Conflicts`.
module ColumnType =
    let private decimalPat =
        Regex(@"Decimal\(\s*(\d+)\s*,\s*\d+\s*\)", RegexOptions.Compiled)

    let private enumPat =
        Regex(@"Enum(8|16)\([^)]*\)", RegexOptions.Compiled)

    /// Normalise a type string by collapsing parameterised forms to their
    /// fixed-width / fixed-name equivalents:
    ///   `Decimal(P, S)`        → `Decimal32` / `64` / `128` / `256`
    ///   `Enum8('a'=1, …)`      → `Enum8`
    ///   `Enum16(…)`            → `Enum16`
    /// Composites recurse via plain string substitution —
    /// `Array(Decimal(9, 2))` becomes `Array(Decimal32)`.
    let normalize (t: string) : string =
        let t1 =
            decimalPat.Replace(t, fun m ->
                match System.Int32.TryParse(m.Groups.[1].Value) with
                | true, p when p < 10 -> "Decimal32"
                | true, p when p < 19 -> "Decimal64"
                | true, p when p < 39 -> "Decimal128"
                | true, p when p < 77 -> "Decimal256"
                | _ -> m.Value)
        enumPat.Replace(t1, fun m -> "Enum" + m.Groups.[1].Value)

    /// True if a server-sent type string and a client column type are
    /// equivalent after normalisation.
    let isCompatible (clientType: string) (serverType: string) : bool =
        normalize clientType = normalize serverType

/// Polymorphic interface every column must satisfy. Mirrors ch-go's
/// `Column = ColResult + ColInput` combined interface (`proto/column.go`):
/// every concrete column type in this codebase has both encode and decode
/// paths so we don't split the interfaces.
type IColumnResult =
    abstract Type : string
    abstract Rows : int
    abstract Reset : unit -> unit
    abstract DecodeColumn : Reader * int -> unit
    abstract EncodeColumn : Buf -> unit

/// Typed refinement of `IColumnResult`. A column that exposes per-row access
/// for some 'T (e.g. ColPrimitive<int32>: IColumnOf<int32>,
/// ColStr: IColumnOf<string>). Needed by ColLowCardinality<'T> to read its
/// dictionary inner and to receive Append calls.
type IColumnOf<'T> =
    inherit IColumnResult
    abstract Append : 'T -> unit
    abstract Row : int -> 'T

/// Optional facet for columns that carry a per-block state header in front
/// of the column body. The Block-level decoder invokes DecodeState before
/// DecodeColumn (encode mirrors). ch-go has the same split as
/// `StateEncoder` / `StateDecoder` (`proto/column.go`).
///
/// Used today only by LowCardinality.
type IStatefulColumn =
    abstract EncodeState : Buf -> unit
    abstract DecodeState : Reader -> unit

/// Optional facet for columns whose wire-level semantics depend on
/// parameters embedded in the server-sent type string — `Enum8(...)`,
/// `DateTime64(N, 'tz')`, `Decimal(P, S)`. The client invokes `Infer` with
/// the full type string before the first column decode so the column can
/// configure itself. Mirrors ch-go's `Inferable` interface
/// (`proto/column.go`).
type IInferable =
    abstract Infer : string -> unit

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
        member this.EncodeColumn(b) = this.EncodeColumn(b)

    interface IColumnOf<'T> with
        member this.Append(v) = this.Append(v)
        member this.Row(i) = this.Row(i)


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
[<Sealed>] type ColInt128()  = inherit ColPrimitive<System.Int128>("Int128")
[<Sealed>] type ColUInt128() = inherit ColPrimitive<System.UInt128>("UInt128")
[<Sealed>] type ColInt256()  = inherit ColPrimitive<Int256>("Int256")
[<Sealed>] type ColUInt256() = inherit ColPrimitive<UInt256>("UInt256")
