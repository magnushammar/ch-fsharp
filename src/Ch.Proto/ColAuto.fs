namespace Ch.Proto

open System

/// `ColAuto` — receive-side wrapper that builds the right concrete column
/// from the server-sent type string at `Infer` time. Useful when the
/// caller doesn't know the column type ahead of time (ad-hoc SELECTs,
/// generic dashboards). Mirrors ch-go's `proto/col_auto.go`.
///
/// Coverage: every primitive, parameterised scalar (`Decimal(P,S)`,
/// `Enum8/16`, `DateTime64(N)`, `Interval{Scale}`, `FixedString(N)`),
/// plus String / UUID / Date / DateTime / IPv4 / IPv6 / Point / Nothing
/// / JSON / BFloat16. Composite types (`Array`, `Nullable`,
/// `LowCardinality`, `Map`, `Tuple`) are intentionally NOT auto-built —
/// they need either reflection or non-generic column variants. Use the
/// explicit column types for composites.
///
/// After `Infer`, the resolved column is reachable via `Inner` (typed by
/// `IColumnResult` only — caller downcasts to read typed values).
[<Sealed>]
type ColAuto() =
    let mutable inner : IColumnResult option = None

    member _.Inner = inner

    member _.Type =
        match inner with
        | Some c -> c.Type
        | None -> "Auto"

    member _.Rows =
        match inner with
        | Some c -> c.Rows
        | None -> 0

    member _.Reset() =
        match inner with
        | Some c -> c.Reset()
        | None -> ()

    member this.Infer(t: string) =
        // Already inferred and still compatible? No-op.
        match inner with
        | Some c when ColumnType.isCompatible c.Type t -> ()
        | _ ->
            inner <- Some (ColAuto.build t)

    member this.DecodeColumn(r: Reader, n: int) =
        match inner with
        | Some c -> c.DecodeColumn(r, n)
        | None ->
            raise (InvalidOperationException(
                "ColAuto: Infer must be called before DecodeColumn"))

    member this.EncodeColumn(b: Buf) =
        match inner with
        | Some c -> c.EncodeColumn(b)
        | None -> ()

    interface IColumnResult with
        member this.Type = this.Type
        member this.Rows = this.Rows
        member this.Reset() = this.Reset()
        member this.DecodeColumn(r, n) = this.DecodeColumn(r, n)
        member this.EncodeColumn(b) = this.EncodeColumn(b)

    interface IInferable with
        member this.Infer(t) = this.Infer(t)

    /// Factory: turn a server-sent type string into the right concrete
    /// column. Parameterised types call `Infer` on the new column before
    /// returning so the column is fully configured.
    static member build (t: string) : IColumnResult =
        let asInfer (c: 'a when 'a :> IColumnResult and 'a :> IInferable) =
            (c :> IInferable).Infer(t)
            c :> IColumnResult

        match t with
        | "Int8" -> ColInt8() :> IColumnResult
        | "Int16" -> ColInt16() :> _
        | "Int32" -> ColInt32() :> _
        | "Int64" -> ColInt64() :> _
        | "Int128" -> ColInt128() :> _
        | "Int256" -> ColInt256() :> _
        | "UInt8" -> ColUInt8() :> _
        | "UInt16" -> ColUInt16() :> _
        | "UInt32" -> ColUInt32() :> _
        | "UInt64" -> ColUInt64() :> _
        | "UInt128" -> ColUInt128() :> _
        | "UInt256" -> ColUInt256() :> _
        | "Float32" -> ColFloat32() :> _
        | "Float64" -> ColFloat64() :> _
        | "BFloat16" -> ColBFloat16() :> _
        | "Bool" -> ColBool() :> _
        | "String" -> ColStr() :> _
        | "JSON" -> ColJSONStr() :> _
        | "Date" -> ColDate() :> _
        | "Date32" -> ColDate32() :> _
        | "DateTime" -> ColDateTime() :> _
        | "UUID" -> ColUUID() :> _
        | "IPv4" -> ColIPv4() :> _
        | "IPv6" -> ColIPv6() :> _
        | "Point" -> ColPoint() :> _
        | "Nothing" -> ColNothing() :> _
        | "Decimal32" -> ColDecimal32() :> _
        | "Decimal64" -> ColDecimal64() :> _
        | "Decimal128" -> ColDecimal128() :> _
        | "Decimal256" -> ColDecimal256() :> _
        | "Enum8" -> ColEnum8() :> _
        | "Enum16" -> ColEnum16() :> _
        | s when s.StartsWith("Decimal(") ->
            // Downcast Decimal(P, S) by precision, mirrors normalize.
            match ColumnType.normalize s with
            | "Decimal32" -> ColDecimal32() :> _
            | "Decimal64" -> ColDecimal64() :> _
            | "Decimal128" -> ColDecimal128() :> _
            | "Decimal256" -> ColDecimal256() :> _
            | other -> raise (NotSupportedException $"ColAuto: bad Decimal '{s}'")
        | s when s.StartsWith("Enum8(") -> asInfer (ColEnum8())
        | s when s.StartsWith("Enum16(") -> asInfer (ColEnum16())
        | s when s.StartsWith("DateTime(") -> ColDateTime() :> _   // strip timezone
        | s when s.StartsWith("DateTime64(") ->
            // Parse precision out of "DateTime64(N)" or "DateTime64(N, 'tz')"
            let inside = s.Substring(11, s.Length - 12)
            let precPart = (inside.Split(',').[0]).Trim()
            match System.Int32.TryParse(precPart) with
            | true, n -> ColDateTime64(n) :> _
            | _ -> raise (FormatException $"ColAuto: bad DateTime64 '{s}'")
        | s when s.StartsWith("FixedString(") ->
            // FixedString(N) — parse N.
            let inside = s.Substring(12, s.Length - 13)
            match System.Int32.TryParse(inside) with
            | true, n -> ColFixedStr(n) :> _
            | _ -> raise (FormatException $"ColAuto: bad FixedString '{s}'")
        | s when s.StartsWith("Interval") ->
            // IntervalSecond, IntervalMinute, … IntervalYear.
            asInfer (ColInterval())
        | _ ->
            raise (NotSupportedException(
                $"ColAuto: cannot auto-build column of type '{t}'. "
                + "Use the explicit column type for composites "
                + "(Array, Nullable, Map, Tuple, LowCardinality)."))
