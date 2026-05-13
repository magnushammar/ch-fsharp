namespace Ch.Proto

open System
open System.IO

/// Block header info. See `proto/block.go:12`.
///
/// Encoded as a tag-id loop: field-id varint → value → … → id 0 terminator.
/// IDs: 1 = overflows (bool), 2 = bucketNum (int32 LE).
[<Struct>]
type BlockInfo =
    { Overflows: bool
      BucketNum: int32 }

    static member Default = { Overflows = false; BucketNum = -1 }

[<RequireQualifiedAccess>]
module BlockInfo =
    [<Literal>]
    let private FieldEnd = 0UL
    [<Literal>]
    let private FieldOverflows = 1UL
    [<Literal>]
    let private FieldBucketNum = 2UL

    let encode (b: Buf) (info: BlockInfo) =
        b.PutUVarInt(FieldOverflows)
        b.PutBool(info.Overflows)
        b.PutUVarInt(FieldBucketNum)
        b.PutInt32(info.BucketNum)
        b.PutUVarInt(FieldEnd)

    let decode (r: Reader) : BlockInfo =
        let mutable overflows = false
        let mutable bucketNum = -1
        let mutable stop = false
        while not stop do
            match r.UVarInt() with
            | 0UL -> stop <- true
            | 1UL -> overflows <- r.Bool()
            | 2UL -> bucketNum <- r.Int32()
            | other -> raise (InvalidDataException $"unknown BlockInfo field {other}")
        { Overflows = overflows; BucketNum = bucketNum }

/// Block decoding. Receive side of `proto/block.go:248` (`DecodeRawBlock`)
/// plus the temp-table-name wrapper from `client.go:226-234`.
///
/// Layout (after ServerCode byte already consumed):
///   temp-table-name : string   (always "" for server-sent blocks)
///   BlockInfo                  (tag-id loop)
///   columns : uvarint
///   rows    : uvarint
///   for each column:
///     name   : string
///     type   : string
///     custom : bool             (must be false — we don't support custom serialization)
///     <column body bytes>
[<RequireQualifiedAccess>]
module Block =

    /// Per-column callback. The callback owns reading the column body bytes
    /// from `reader`.
    type ColumnHandler = string -> string -> int -> unit

    /// Decode a full block, invoking `columnHandler` once per column. Returns
    /// (columns, rows). On an empty block (columns=0, rows=0) the handler is
    /// not called.
    let decode (reader: Reader) (columnHandler: ColumnHandler) : struct (int * int) =
        let tempTable = reader.Str()
        if tempTable <> "" then
            raise (InvalidDataException $"unexpected temp table name '{tempTable}'")
        let _info = BlockInfo.decode reader
        let columns = reader.Int()
        let rows = reader.Int()
        for _ in 0 .. columns - 1 do
            let name = reader.Str()
            let typ = reader.Str()
            let custom = reader.Bool()
            if custom then
                raise (InvalidDataException "column has custom serialization (not supported)")
            columnHandler name typ rows
        struct (columns, rows)


/// Skips a column body off the wire. Knows the wire size of every primitive
/// type that can appear in `ServerCodeLog` / `ServerProfileEvents` blocks so
/// the parser stays in sync even when we don't decode them.
///
/// We deliberately reject anything non-primitive: if the server emits a
/// `Map(...)`, `Array(...)`, `Nullable(...)`, `LowCardinality(...)` etc. in a
/// telemetry packet we'd rather hard-fail than silently drift. Those cases
/// only appear via user SELECTs which we route through real column decoders.
[<RequireQualifiedAccess>]
module ColumnSkip =

    /// For fixed-size primitive types: bytes per row. Returns -1 for "not a
    /// known fixed-size scalar — caller must do a variable-length walk".
    let private fixedWidth (typ: string) : int =
        match typ with
        | "Int8" | "UInt8" | "Bool" -> 1
        | "Int16" | "UInt16" -> 2
        | "Int32" | "UInt32" | "Date32" | "DateTime" | "Float32" | "IPv4" -> 4
        | "Int64" | "UInt64" | "DateTime64" | "Float64" -> 8
        | "UUID" | "IPv6" | "Int128" | "UInt128" -> 16
        | "Int256" | "UInt256" -> 32
        | "Date" -> 2
        | s when s.StartsWith("DateTime64(") -> 8
        | s when s.StartsWith("DateTime(") -> 4
        | s when s.StartsWith("Decimal32(") -> 4
        | s when s.StartsWith("Decimal64(") -> 8
        | s when s.StartsWith("Decimal128(") -> 16
        | s when s.StartsWith("Decimal256(") -> 32
        | s when s.StartsWith("Enum8(") -> 1
        | s when s.StartsWith("Enum16(") -> 2
        | s when s.StartsWith("FixedString(") ->
            // parse N
            let inner = s.Substring(12, s.Length - 13)
            match Int32.TryParse(inner) with
            | true, n -> n
            | _ -> -1
        | _ -> -1

    /// Skip `rows` worth of column body for the given type string.
    let skip (r: Reader) (typ: string) (rows: int) =
        if rows = 0 then () else
        let w = fixedWidth typ
        if w > 0 then
            r.Skip(rows * w)
        elif typ = "String" then
            // Variable-length: per-row uvarint length + that many bytes.
            for _ in 0 .. rows - 1 do
                let n = r.Int()
                if n > 0 then r.Skip(n)
        else
            // Not supported in MVP skip path. We'd rather fail loudly than
            // silently misread later bytes.
            raise (InvalidDataException $"cannot skip column of type '{typ}'")
