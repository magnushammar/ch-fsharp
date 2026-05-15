namespace Ch.Proto

open System
open System.Collections.Generic

/// `Enum8` — Int8 wire format with a name⇆int mapping stored on the column.
/// Server-sent type carries the mapping: `Enum8('off' = 0, 'on' = 1, …)`.
/// Use `Infer(typeString)` to populate the mapping from the server, or pass
/// the definitions to the constructor.
///
/// `Append(name)` looks up the wire value via the map; `Row(i)` returns the
/// name. `AppendRaw` / `RawValue` skip the map for hot-path access.
/// ch-go reference: `proto/col_enum.go`.
[<Sealed>]
type ColEnum8() =
    let raw = ColInt8()
    let nameToValue = Dictionary<string, int8>()
    let valueToName = Dictionary<int8, string>()
    let mutable typeStr = "Enum8"

    new (definitions: (string * int8) seq) as this =
        ColEnum8()
        then
            for (name, value) in definitions do
                this.AddDefinition(name, value)
            this.RefreshTypeString()

    member private _.AddDefinition(name: string, value: int8) =
        nameToValue.[name] <- value
        valueToName.[value] <- name

    member private _.RefreshTypeString() =
        let parts =
            valueToName
            |> Seq.sortBy (fun kv -> kv.Key)
            |> Seq.map (fun kv -> $"'{kv.Value}' = {kv.Key}")
        typeStr <- "Enum8(" + String.concat ", " parts + ")"

    member _.Type = typeStr
    member _.Rows = raw.Rows

    /// Underlying Int8 column. Useful for bulk operations bypassing the map.
    member _.Raw = raw
    /// Read-only view of the name→value map.
    member _.NameToValue = nameToValue :> IReadOnlyDictionary<string, int8>
    /// Read-only view of the value→name map.
    member _.ValueToName = valueToName :> IReadOnlyDictionary<int8, string>

    member _.Reset() = raw.Reset()

    /// Append by name. Throws if the name isn't in the mapping.
    member _.Append(name: string) =
        let mutable v = 0y
        if not (nameToValue.TryGetValue(name, &v)) then
            raise (KeyNotFoundException $"unknown enum name '{name}'")
        raw.Append(v)

    /// Append by raw int value. Skips the map.
    member _.AppendRaw(v: int8) = raw.Append(v)

    /// Read name at row i. Throws if the wire value isn't in the mapping.
    member _.Row(i: int) : string =
        let v = raw.Row(i)
        let mutable name = ""
        if not (valueToName.TryGetValue(v, &name)) then
            raise (KeyNotFoundException $"unknown enum value {v}")
        name

    /// Read raw int value at row i.
    member _.RawValue(i: int) : int8 = raw.Row(i)

    member _.DecodeColumn(r: Reader, n: int) = raw.DecodeColumn(r, n)
    member _.EncodeColumn(b: Buf) = raw.EncodeColumn(b)

    member this.Infer(t: string) =
        let openParen = t.IndexOf('(')
        let lastParen = t.LastIndexOf(')')
        if openParen < 0 || lastParen <= openParen then
            raise (FormatException $"bad enum type string: '{t}'")
        nameToValue.Clear()
        valueToName.Clear()
        let inner = t.Substring(openParen + 1, lastParen - openParen - 1)
        // Quote-aware split / lookup — `Enum8('a,b' = 1)` (quoted comma
        // in the name) and `Enum8('a=b' = 1)` (quoted `=`) both parse
        // correctly via `CompositeTypeString`.
        for def in CompositeTypeString.splitTopLevel ',' inner do
            let eq = CompositeTypeString.findTopLevel '=' def
            if eq < 0 then raise (FormatException $"bad enum def: '{def}'")
            let name = def.Substring(0, eq).Trim().Trim('\'')
            let value = System.SByte.Parse(def.Substring(eq + 1).Trim())
            this.AddDefinition(name, value)
        typeStr <- t

    interface IColumnResult with
        member this.Type = this.Type
        member this.Rows = this.Rows
        member this.Reset() = this.Reset()
        member this.DecodeColumn(r, n) = this.DecodeColumn(r, n)
        member this.EncodeColumn(b) = this.EncodeColumn(b)

    interface IColumnOf<string> with
        member this.Append(v) = this.Append(v)
        member this.Row(i) = this.Row(i)

    interface IInferable with
        member this.Infer(t) = this.Infer(t)

    interface IArrayable with
        member this.Array() = ColArr<string>(this) :> IColumnResult

    interface INullable with
        member this.Nullable() = ColNullable<string>(this) :> IColumnResult

    interface ILowCardinality with
        member this.LowCardinality() = ColLowCardinality<string>(this) :> IColumnResult


/// `Enum16` — Int16 wire format with name⇆int mapping. Same shape as
/// `ColEnum8` but 16-bit wire values, range -32768..32767.
[<Sealed>]
type ColEnum16() =
    let raw = ColInt16()
    let nameToValue = Dictionary<string, int16>()
    let valueToName = Dictionary<int16, string>()
    let mutable typeStr = "Enum16"

    new (definitions: (string * int16) seq) as this =
        ColEnum16()
        then
            for (name, value) in definitions do
                this.AddDefinition(name, value)
            this.RefreshTypeString()

    member private _.AddDefinition(name: string, value: int16) =
        nameToValue.[name] <- value
        valueToName.[value] <- name

    member private _.RefreshTypeString() =
        let parts =
            valueToName
            |> Seq.sortBy (fun kv -> kv.Key)
            |> Seq.map (fun kv -> $"'{kv.Value}' = {kv.Key}")
        typeStr <- "Enum16(" + String.concat ", " parts + ")"

    member _.Type = typeStr
    member _.Rows = raw.Rows
    member _.Raw = raw
    member _.NameToValue = nameToValue :> IReadOnlyDictionary<string, int16>
    member _.ValueToName = valueToName :> IReadOnlyDictionary<int16, string>

    member _.Reset() = raw.Reset()

    member _.Append(name: string) =
        let mutable v = 0s
        if not (nameToValue.TryGetValue(name, &v)) then
            raise (KeyNotFoundException $"unknown enum name '{name}'")
        raw.Append(v)

    member _.AppendRaw(v: int16) = raw.Append(v)

    member _.Row(i: int) : string =
        let v = raw.Row(i)
        let mutable name = ""
        if not (valueToName.TryGetValue(v, &name)) then
            raise (KeyNotFoundException $"unknown enum value {v}")
        name

    member _.RawValue(i: int) : int16 = raw.Row(i)

    member _.DecodeColumn(r: Reader, n: int) = raw.DecodeColumn(r, n)
    member _.EncodeColumn(b: Buf) = raw.EncodeColumn(b)

    member this.Infer(t: string) =
        let openParen = t.IndexOf('(')
        let lastParen = t.LastIndexOf(')')
        if openParen < 0 || lastParen <= openParen then
            raise (FormatException $"bad enum type string: '{t}'")
        nameToValue.Clear()
        valueToName.Clear()
        let inner = t.Substring(openParen + 1, lastParen - openParen - 1)
        // Quote-aware split / lookup — `Enum8('a,b' = 1)` (quoted comma
        // in the name) and `Enum8('a=b' = 1)` (quoted `=`) both parse
        // correctly via `CompositeTypeString`.
        for def in CompositeTypeString.splitTopLevel ',' inner do
            let eq = CompositeTypeString.findTopLevel '=' def
            if eq < 0 then raise (FormatException $"bad enum def: '{def}'")
            let name = def.Substring(0, eq).Trim().Trim('\'')
            let value = System.Int16.Parse(def.Substring(eq + 1).Trim())
            this.AddDefinition(name, value)
        typeStr <- t

    interface IColumnResult with
        member this.Type = this.Type
        member this.Rows = this.Rows
        member this.Reset() = this.Reset()
        member this.DecodeColumn(r, n) = this.DecodeColumn(r, n)
        member this.EncodeColumn(b) = this.EncodeColumn(b)

    interface IColumnOf<string> with
        member this.Append(v) = this.Append(v)
        member this.Row(i) = this.Row(i)

    interface IInferable with
        member this.Infer(t) = this.Infer(t)

    interface IArrayable with
        member this.Array() = ColArr<string>(this) :> IColumnResult

    interface INullable with
        member this.Nullable() = ColNullable<string>(this) :> IColumnResult

    interface ILowCardinality with
        member this.LowCardinality() = ColLowCardinality<string>(this) :> IColumnResult
