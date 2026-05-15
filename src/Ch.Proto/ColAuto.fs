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
/// / JSON / BFloat16. Composites are handled via facet-interface dispatch
/// (`IArrayable` / `INullable` / `ILowCardinality`) — `Array(T)` /
/// `Nullable(T)` / `LowCardinality(T)` / `Tuple(...)` recurse through
/// their inner types. `Map(K,V)` is hardcoded for the common
/// `Map(String, String)` combination; general `Map(K,V)` is deferred.
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
    /// returning so the column is fully configured. Composites dispatch
    /// via the `IArrayable` / `INullable` / `ILowCardinality` facets on
    /// the recursively-built inner column.
    static member build (t: string) : IColumnResult =
        let asInfer (c: 'a when 'a :> IColumnResult and 'a :> IInferable) =
            (c :> IInferable).Infer(t)
            c :> IColumnResult

        // Strip the outermost wrapper: peelOuter "Array" "Array(Int32)" -> "Int32".
        let peelOuter (prefix: string) (s: string) =
            s.Substring(prefix.Length + 1, s.Length - prefix.Length - 2)

        // Split "Tuple(T1, T2, Tuple(T3, T4), ...)" on top-level commas only —
        // commas inside nested composites are not separators.
        let parseTupleInners (s: string) : string list =
            let body = s.Substring(6, s.Length - 7)  // strip "Tuple(" and ")"
            let acc = ResizeArray<string>()
            let mutable startIdx = 0
            let mutable depth = 0
            for i in 0 .. body.Length - 1 do
                match body.[i] with
                | '(' -> depth <- depth + 1
                | ')' -> depth <- depth - 1
                | ',' when depth = 0 ->
                    acc.Add(body.Substring(startIdx, i - startIdx).Trim())
                    startIdx <- i + 1
                | _ -> ()
            acc.Add(body.Substring(startIdx).Trim())
            List.ofSeq acc

        // Split "Map(K, V)" on the top-level comma. K or V may themselves be
        // composites containing nested commas.
        let parseMapInners (s: string) : string * string =
            let body = s.Substring(4, s.Length - 5)  // strip "Map(" and ")"
            let mutable depth = 0
            let mutable splitAt = -1
            let mutable i = 0
            while i < body.Length && splitAt < 0 do
                match body.[i] with
                | '(' -> depth <- depth + 1
                | ')' -> depth <- depth - 1
                | ',' when depth = 0 -> splitAt <- i
                | _ -> ()
                i <- i + 1
            if splitAt < 0 then
                raise (NotSupportedException $"ColAuto: malformed Map type '{s}'")
            body.Substring(0, splitAt).Trim(),
            body.Substring(splitAt + 1).Trim()

        // Discover the typed inner of a column by walking its IColumnOf<'X>
        // interface impl. Returns 'X so we can MakeGenericType ColMap<K,V>.
        let innerTypeOf (col: IColumnResult) : System.Type =
            col.GetType().GetInterfaces()
            |> Array.tryFind (fun ifc ->
                ifc.IsGenericType
                && ifc.GetGenericTypeDefinition() = typedefof<IColumnOf<_>>)
            |> Option.map (fun ifc -> ifc.GetGenericArguments().[0])
            |> Option.defaultWith (fun () ->
                raise (NotSupportedException
                    $"ColAuto: column '{col.Type}' does not implement IColumnOf<'T>; cannot be a Map key/value"))

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
            | _ -> raise (NotSupportedException $"ColAuto: bad Decimal '{s}'")
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

        // ── Composite branches — facet-interface dispatch ─────────────

        | "Nullable(Nothing)" ->
            // ColNothing is a special placeholder; wrap explicitly.
            ColNullable<Nothing>(ColNothing()) :> _

        | "Map(String, String)" ->
            // Common Map combo on a static fast path — avoids reflection
            // entirely for what's likely the hottest Map() case.
            ColMap<string, string>(ColStr(), ColStr()) :> _

        | s when s.StartsWith("Array(") ->
            let innerCol = ColAuto.build (peelOuter "Array" s)
            match innerCol with
            | :? IArrayable as a -> a.Array()
            | _ ->
                raise (NotSupportedException
                    $"ColAuto: cannot build Array of '{innerCol.Type}'")

        | s when s.StartsWith("Nullable(") ->
            let innerCol = ColAuto.build (peelOuter "Nullable" s)
            match innerCol with
            | :? INullable as n -> n.Nullable()
            | _ ->
                raise (NotSupportedException
                    $"ColAuto: cannot build Nullable of '{innerCol.Type}'")

        | s when s.StartsWith("LowCardinality(") ->
            let innerCol = ColAuto.build (peelOuter "LowCardinality" s)
            match innerCol with
            | :? ILowCardinality as lc -> lc.LowCardinality()
            | _ ->
                raise (NotSupportedException
                    $"ColAuto: cannot build LowCardinality of '{innerCol.Type}'")

        | s when s.StartsWith("Tuple(") ->
            let innerStrs = parseTupleInners s
            let inners =
                innerStrs |> List.map ColAuto.build |> List.toArray
            ColTuple(inners) :> _

        | s when s.StartsWith("Map(") ->
            // General Map(K, V) — recursively build the key and value
            // columns, then ColMap<K, V> via one-time reflection at
            // construction. No hot-path cost: the constructed
            // ColMap<K, V> runs through the static fast path forever
            // after.
            let (kStr, vStr) = parseMapInners s
            let keyCol = ColAuto.build kStr
            let valCol = ColAuto.build vStr
            let kT = innerTypeOf keyCol
            let vT = innerTypeOf valCol
            let mapType = typedefof<ColMap<_, _>>.MakeGenericType(kT, vT)
            let instance =
                try
                    System.Activator.CreateInstance(mapType, keyCol, valCol)
                with
                | :? System.Reflection.TargetInvocationException as ex ->
                    // Unwrap reflection wrapper so constraint violations
                    // (e.g. 'K not equality + not null) surface clearly.
                    match ex.InnerException with
                    | null -> reraise ()
                    | inner -> raise inner
            match instance with
            | null ->
                raise (NotSupportedException
                    $"ColAuto: ColMap construction for '{s}' returned null")
            | inst -> inst :?> IColumnResult

        | _ ->
            raise (NotSupportedException
                $"ColAuto: cannot auto-build column of type '{t}'.")
