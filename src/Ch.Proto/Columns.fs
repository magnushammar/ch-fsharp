namespace Ch.Proto

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

    let private dateTimeTzPat =
        Regex(@"DateTime\('[^']*'\)", RegexOptions.Compiled)

    let private dateTime64TzPat =
        Regex(@"DateTime64\(\s*(\d+)\s*,\s*'[^']*'\s*\)", RegexOptions.Compiled)

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
        let t2 = enumPat.Replace(t1, fun m -> "Enum" + m.Groups.[1].Value)
        // Strip timezones — `DateTime('UTC')` → `DateTime`,
        // `DateTime64(3, 'UTC')` → `DateTime64(3)`. The precision is kept
        // because it determines the on-wire scale.
        let t3 = dateTimeTzPat.Replace(t2, "DateTime")
        dateTime64TzPat.Replace(t3, fun m -> sprintf "DateTime64(%s)" m.Groups.[1].Value)

    /// True if a server-sent type string and a client column type are
    /// equivalent after normalisation.
    let isCompatible (clientType: string) (serverType: string) : bool =
        normalize clientType = normalize serverType

/// Shared parsing helpers for ClickHouse type strings.
///
/// ClickHouse type strings nest parens (`Array(Nullable(Int32))`) and
/// embed single-quoted strings (`Enum8('foo' = 1)`, `DateTime('UTC')`).
/// Naive `String.Split(',')` and `IndexOf('=')` lookups mis-handle
/// quoted regions: e.g. `Enum8('a,b' = 1, 'c' = 2)` has a quoted comma
/// inside the first name, and `Enum8('a=b' = 1)` has a quoted `=`.
///
/// Both `ColAuto.build` (Tuple / Map outer splits) and `ColEnum.Infer`
/// (name / value def lookups) need quote-and-depth-aware scanning;
/// these helpers centralise that logic.
module CompositeTypeString =
    /// Split `s` at occurrences of `sep` that are simultaneously at
    /// paren-depth 0 and outside any single-quoted region. Returns the
    /// segments in order, including a final segment after the last
    /// separator (empty string if `s` ends with `sep`).
    let splitTopLevel (sep: char) (s: string) : string list =
        let acc = System.Collections.Generic.List<string>()
        let mutable startIdx = 0
        let mutable depth = 0
        let mutable inQuote = false
        for i in 0 .. s.Length - 1 do
            let c = s.[i]
            if inQuote then
                if c = '\'' then inQuote <- false
            else
                match c with
                | '\'' -> inQuote <- true
                | '(' -> depth <- depth + 1
                | ')' -> depth <- depth - 1
                | ch when ch = sep && depth = 0 ->
                    acc.Add(s.Substring(startIdx, i - startIdx))
                    startIdx <- i + 1
                | _ -> ()
        acc.Add(s.Substring(startIdx))
        List.ofSeq acc

    /// Index of the first occurrence of `target` at paren-depth 0 and
    /// outside any single-quoted region. Returns -1 if none. Used by
    /// `ColEnum.Infer` to find the `=` between an enum name and its
    /// integer value, ignoring `=` characters embedded in the name.
    let findTopLevel (target: char) (s: string) : int =
        let mutable depth = 0
        let mutable inQuote = false
        let mutable found = -1
        let mutable i = 0
        while i < s.Length && found < 0 do
            let c = s.[i]
            if inQuote then
                if c = '\'' then inQuote <- false
            else
                match c with
                | '\'' -> inQuote <- true
                | '(' -> depth <- depth + 1
                | ')' -> depth <- depth - 1
                | ch when ch = target && depth = 0 -> found <- i
                | _ -> ()
            i <- i + 1
        found

/// Polymorphic interface every column must satisfy. Mirrors ch-go's
/// `Column = ColResult + ColInput` combined interface (`proto/column.go`):
/// every concrete column type in this codebase has both encode and decode
/// paths so we don't split the interfaces.
type IColumnResult =
    abstract Type : string
    abstract Rows : int
    abstract Reset : unit -> unit
    /// Decode `n` rows from the reader **in-place**, replacing the column's
    /// previous contents. The decoded data is owned by the column and is
    /// valid only until the next `DecodeColumn` or `Reset` — callers must not
    /// retain views of it past the block that produced them.
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

/// Composite-construction facet: column can be wrapped in `Array(_)` by
/// `ColAuto`. Returns an `IColumnResult` whose runtime type is `ColArr<'T>`
/// for the implementer's `'T` — callers downcast to read typed rows.
type IArrayable =
    abstract Array : unit -> IColumnResult

/// Composite-construction facet: column can be wrapped in `Nullable(_)` by
/// `ColAuto`. Returns an `IColumnResult` whose runtime type is
/// `ColNullable<'T>` for the implementer's `'T`.
type INullable =
    abstract Nullable : unit -> IColumnResult

/// Composite-construction facet: column can be wrapped in `LowCardinality(_)`
/// by `ColAuto`. Returns an `IColumnResult` whose runtime type is
/// `ColLowCardinality<'T>` for the implementer's `'T`. Implementer's `'T`
/// must satisfy `equality + not null`.
type ILowCardinality =
    abstract LowCardinality : unit -> IColumnResult

/// Bulk-append facet — column accepts a contiguous span of `'T` in one
/// shot. `ColPrimitive<'T>` implements this via `MemoryMarshal.Cast` for
/// zero-copy byte writes. Composite wrappers (`ColArr` / `ColNullable`)
/// dispatch to inner when present and fall back to per-row `Append`
/// otherwise. Lifetime: span is consumed during the call; no aliasing.
type IBulkAppendable<'T> =
    abstract AppendRange : System.ReadOnlySpan<'T> -> unit

/// Bulk-read facet — column exposes its row buffer as a typed span
/// aliasing internal storage. `ColPrimitive<'T>` implements this via
/// `MemoryMarshal.Cast` over the raw byte buffer. Lifetime: returned
/// span is valid until the next `DecodeColumn` / `Reset` / `Append` /
/// `AppendRange` on the column.
type IBulkReadable<'T> =
    abstract AsSpan : unit -> System.ReadOnlySpan<'T>

/// Per-row raw byte view, irrespective of the column's typed `'T`.
/// Implemented by `ColStr` (UTF-8 bytes) and the `ColFixedBytes` family
/// (fixed-N bytes). Lets byte-land consumers skip UTF-8 decoding /
/// `.ToArray()` materialisation; also the substrate
/// `ColLowCardinality.RowSpan` dispatches through.
type IRowBytes =
    abstract RowBytes : int -> System.ReadOnlySpan<byte>
