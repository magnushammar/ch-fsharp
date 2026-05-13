# ch-fsharp

A low-level, columnar ClickHouse client for F# on .NET 10, ported from
[ClickHouse/ch-go](https://github.com/ClickHouse/ch-go) (Apache 2.0, copyright
ClickHouse, Inc. and The Go Faster Authors). Performance > ergonomics. Native
TCP protocol (revision 54460), zero-copy column decode, LZ4 compression, INSERT
and SELECT. Build / test / run requires .NET 10 SDK and a reachable ClickHouse
server (default `localhost:9000`).

```bash
dotnet test Ch.slnx                                 # 200+ tests
dotnet run --project src/Ch.Bench.Numbers -- --ping # smoke handshake
```

---

## Connecting

```fsharp
open Ch.Proto
open Ch.Client

let opts =
    { ChOptions.defaults with
        Address  = "localhost:9000"
        Database = "default"
        User     = "default"
        Password = System.Environment.GetEnvironmentVariable("CLICKHOUSE_PASSWORD")
        ClientName  = "myapp/1.0"
        Compression = false }   // set true to negotiate LZ4

use cts = new System.Threading.CancellationTokenSource()
use client = Client.ConnectAsync(opts, cts.Token).GetAwaiter().GetResult()

client.PingAsync(cts.Token).GetAwaiter().GetResult()
```

`Client` wraps one TCP connection. It is **not thread-safe** — use one
instance per concurrent query. Disposing the client closes the socket.

## Running queries

Every query is described by a `ChQuery` record. Start from
`ChQuery.defaults` and override only the fields you need:

```fsharp
{ ChQuery.defaults with
    Body     = "SELECT ..."
    Results  = [...]            // SELECT — caller's target columns
    Input    = [...]            // INSERT — caller's source columns
    OnBlock  = fun rows -> ()   // SELECT — invoked per data block
    OnInput  = None             // INSERT — multi-block streaming callback
    Settings = [...]            // query-scope key=value pairs
    QueryId  = None             // optional UUID; auto-generated if None
}
```

Pass to `client.DoAsync(query, ct)`. The same call handles SELECT, INSERT,
and DDL — the mode is inferred from which fields you populated.

## SELECT — minimal

```fsharp
let n = ColInt32()
let s = ColStr()
let mutable totalRows = 0
let onBlock (rows: int) =
    for i in 0 .. rows - 1 do
        printfn "%d %s" (n.Row(i)) (s.Row(i))
        totalRows <- totalRows + 1

let q = { ChQuery.defaults with
            Body = "SELECT toInt32(number) AS n, toString(number) AS s
                    FROM system.numbers LIMIT 1000"
            Results = [ { Name = "n"; Column = n }
                        { Name = "s"; Column = s } ]
            OnBlock = onBlock }

client.DoAsync(q, ct).GetAwaiter().GetResult()
```

Each `ColumnResult` has a `Name` (matched against the server's column
name; pass `""` to accept by index only) and a `Column` instance that
the driver decodes into in-place on every block. `OnBlock` runs after
all columns are filled.

## INSERT — minimal

```fsharp
let id    = ColInt32()
let name  = ColStr()
let score = ColFloat64()

id.Append(1);    name.Append("alpha"); score.Append(1.5)
id.Append(2);    name.Append("beta");  score.Append(2.5)

let q = { ChQuery.defaults with
            Body = "INSERT INTO my_table VALUES"
            Input = [ { Name = "id";    Column = id }
                      { Name = "name";  Column = name }
                      { Name = "score"; Column = score } ] }

client.DoAsync(q, ct).GetAwaiter().GetResult()
```

### Multi-block streaming insert

For sources too large to hold in memory, set `OnInput = Some next`. The
key thing to internalise — and the bit that's easy to miss from the
function signature alone — is **when** `next` fires:

1. Caller pre-populates the Input columns *before* `DoAsync`.
2. `DoAsync` sends the query and receives the server's schema header
   (used to drive `Infer` on each Input column — sets enum maps,
   decimal scales, etc.).
3. Driver encodes whatever's in the columns NOW as **block #1** and
   flushes it.
4. Driver calls `next()` — i.e. **`next` fires AFTER each block is on
   the wire, not before**. The block you just sent is already gone; you
   cannot amend it. The job of `next` is to (a) clear the column
   buffers and (b) refill them with the next batch for **block #2**.
5. Return `true` → encode block #2, flush, call `next()` again. Return
   `false` → driver writes the blank end-of-data marker; server commits.

```fsharp
let id    = ColInt32()
let value = ColFloat64()

// Pull the next batch from your data source into the Input columns.
// Returns how many rows were appended this call.
let fillNext (batchSize: int) : int =
    let mutable n = 0
    while n < batchSize && hasMoreRows () do
        let r = readNextRow ()
        id.Append(r.Id)
        value.Append(r.Value)
        n <- n + 1
    n

// (1) Pre-fill block #1 — these rows go on the wire before next() ever runs.
fillNext 100_000 |> ignore

// (4) Reset clears the row buffer; the LowCardinality dictionary, if any,
// is rebuilt fresh from the buffer on every EncodeColumn so each block is
// self-contained on the wire. The driver does NOT reset Input columns for
// you between blocks.
let onInput () =
    id.Reset()
    value.Reset()
    fillNext 100_000 > 0     // true: send another block; false: end stream.

let q = { ChQuery.defaults with
            Body    = "INSERT INTO my_table VALUES"
            Input   = [ { Name = "id";    Column = id }
                        { Name = "value"; Column = value } ]
            OnInput = Some onInput }

client.DoAsync(q, ct).GetAwaiter().GetResult()
```

Common slip: starting with empty columns and expecting `next` to fill
the first batch. It won't — `next` only fires *after* a block has been
sent, so block #1 would be empty. Always pre-fill before `DoAsync`.

**Without OnInput** the contents of the Input columns at `DoAsync` time
are sent as a single block. Use this for fixed-size payloads that
already fit in memory.

**OnBlock vs OnInput.** `OnBlock` fires for every server `Data` block
during SELECT, after all decode targets in `Results` are filled. It
does not fire for INSERT — the server's only `Data` packet for an
INSERT is the schema header (rows=0), which the driver consumes
internally to infer Input column types.

Note: on databases with `ENGINE = Atomic` (the default), DDL is async —
add a short sleep or a synchronisation query between `CREATE TABLE` and
your INSERT, otherwise the visibility race intermittently truncates the
result set.

---

## Column types reference

Every column implements `IColumnResult` (Type/Rows/Reset/Decode/Encode).
Typed columns also implement `IColumnOf<'T>` (Append/Row of `'T`).

### Integers and floats

| F# type | ClickHouse | Underlying |
|---|---|---|
| `ColInt8` `ColInt16` `ColInt32` `ColInt64` `ColInt128` `ColInt256` | Int8 … Int256 | LE bytes |
| `ColUInt8` `ColUInt16` `ColUInt32` `ColUInt64` `ColUInt128` `ColUInt256` | UInt8 … UInt256 | LE bytes |
| `ColFloat32` `ColFloat64` | Float32 / Float64 | IEEE 754 LE |
| `ColBool` | Bool | 1 byte 0/1 |

All inherit a generic `ColPrimitive<'T>` and expose
`AsSpan() : ReadOnlySpan<'T>` for zero-copy block scans.

`Int128`/`UInt128` use `System.Int128` / `System.UInt128`. `Int256`/
`UInt256` use a 2-`UInt128` struct (`Ch.Proto.Int256`, `Ch.Proto.UInt256`)
since .NET 10 has no native 256-bit type. Decimal256 reuses `Int256`.

```fsharp
let v = ColUInt64()
v.Append(42UL)
v.AsSpan().[0]   // 42UL — zero-copy
```

### BFloat16

`ColBFloat16` inherits `ColPrimitive<uint16>` (the wire format). Use the
helpers for `float32`-typed access:

```fsharp
let bf = ColBFloat16()
bf.AppendFloat(1.5f)
bf.RowFloat(0)   // 1.5f
bf.Row(0)        // 0x3FC0us — raw BFloat16 bits
```

BFloat16 has 7 mantissa bits — round-trip via `RowFloat` is lossy.

### String

`ColStr` is variable-length UTF-8 (length-prefixed varint + bytes).

```fsharp
let s = ColStr()
s.Append("hello")
s.Row(0)   // "hello"
```

### FixedString(N)

`ColFixedStr(n)` — N bytes per row, no length prefix.

```fsharp
let fs = ColFixedStr(8)
fs.AppendBytes(ReadOnlySpan [| 'h'B; 'e'B; 'l'B; 'l'B; 'o'B; 0uy; 0uy; 0uy |])
fs.RowSpan(0)   // 8-byte ReadOnlySpan
```

### Date / Date32 / DateTime / DateTime64

| Column | Underlying | .NET helper |
|---|---|---|
| `ColDate` | UInt16 days since 1970-01-01 | `AppendDate(DateOnly)` / `RowDate` |
| `ColDate32` | Int32 days since 1970-01-01 | same |
| `ColDateTime` | UInt32 seconds since epoch | `AppendDateTime(DateTime)` / `RowDateTime` |
| `ColDateTime64(n)` | Int64 ticks at 10^-n s | `AppendDateTime(DateTime)` / `RowDateTime` |

`ColDateTime64` requires a precision (0..9) at construction unless
you only need raw `Int64` access — `ColDateTime64()` leaves precision
unset and throws if you call the typed helpers.

### IPv4 / IPv6 / UUID

```fsharp
let v4 = ColIPv4()
v4.AppendIP(System.Net.IPAddress.Parse("192.168.1.1"))
v4.RowIP(0)

let v6 = ColIPv6()
let id = ColUUID()
id.AppendBytes(ReadOnlySpan(guidBytes))   // 16 bytes
```

UUID encode/decode swaps the two halves to match ClickHouse's mixed-
endian layout.

### Decimal

`ColDecimal32/64/128/256` are wire-identical to the underlying integer
columns; scale is the caller's concern (matches ch-go). Convert via the
`Decimal` module:

```fsharp
let d = ColDecimal32()                 // Decimal(9, 2) on the wire
d.Append(Decimal.toInt32 12.34m 2)     // raw = 1234
Decimal.fromInt32 (d.Row(0)) 2         // 12.34m
```

Helpers: `fromInt32`/`fromInt64`/`toInt32`/`toInt64`. Decimal128 with
> 28 digits overflows `System.Decimal` — work with the raw `Int128`.

### Enum8 / Enum16

The server-sent type string carries the mapping; the driver auto-runs
`Infer` before the first decode. Pre-construct an empty `ColEnum8()`
for SELECT, or pass an explicit mapping for INSERT:

```fsharp
// SELECT — mapping comes from the server
let en = ColEnum8()
// after DoAsync: en.Row(0) returns the name string
// en.RawValue(0) returns the int8 wire value

// INSERT — mapping must be set on the column
let en2 = ColEnum8([| "off", 0y; "on", 1y; "dim", 2y |])
en2.Append("on")    // raw becomes 1y
```

`ColEnum16` is the same shape with `int16` wire.

### Interval

`ColInterval` wraps an `Int64` with a scale tag (Second … Year).
On SELECT the scale is inferred from the type string (`IntervalDay`,
`IntervalSecond`, …); on INSERT set it via the constructor:

```fsharp
let iv = ColInterval(Day)
iv.Append({ Scale = Day; Value = 7L })
iv.Row(0)   // { Scale = Day; Value = 7L }
```

### Point (Geo)

`ColPoint` — two parallel `Float64` columns under Type "Point".

```fsharp
let pt = ColPoint()
pt.Append({ X = 1.5; Y = 2.5 })
pt.Row(0)   // { X = 1.5; Y = 2.5 }
```

### JSON

`ColJSONStr` is a `ColStr` plus a UInt64 serialization-version state
header. Requires `output_format_native_write_json_as_string = 1` on the
query Settings:

```fsharp
let js = ColJSONStr()
js.Append("""{"a":1}""")
js.Row(0)

// in the query:
Settings = [ { Key = "output_format_native_write_json_as_string"
               Value = "1"; Important = false }
             { Key = "allow_experimental_json_type"
               Value = "1"; Important = false } ]
```

### Nothing

`ColNothing` is the 1-byte-per-row placeholder for `Nullable(Nothing)`
(what `SELECT NULL` returns).

```fsharp
let n = ColNullable<Nothing>(ColNothing())
// reads ValueNone for every row
```

---

## Composite types

Composite columns wrap one (or two) inner columns. The inner must
implement `IColumnOf<'T>` for the inner element type.

### Array(T)

`ColArr<'T>(inner)` — variable-length list per row. Recursive: the inner
can itself be a `ColArr`.

```fsharp
let arr = ColArr<int32>(ColInt32())
arr.Append([| 1; 2; 3 |])
arr.Row(0)   // [| 1; 2; 3 |]

let aoa = ColArr<int32 array>(ColArr<int32>(ColInt32()))
aoa.Append([| [| 1; 2 |]; [| 3; 4; 5 |] |])
```

### Nullable(T)

`ColNullable<'T>(inner)` — per-row null mask. Reads / writes
`'T voption`.

```fsharp
let n = ColNullable<int32>(ColInt32())
n.Append(ValueSome 42)
n.Append(ValueNone)
n.Row(0)   // ValueSome 42
n.Row(1)   // ValueNone
```

### Map(K, V)

`ColMap<'K, 'V>(keys, values)` — same offset table as Array, two parallel
inner columns. `'K` must be `equality` + `not null`. Reads as
`Dictionary<'K, 'V>`; `RowKV(i)` returns an ordered
`KeyValuePair<'K, 'V> array`.

```fsharp
let m = ColMap<string, int32>(ColStr(), ColInt32())
m.AppendKV([| KeyValuePair("a", 1); KeyValuePair("b", 2) |])
m.Row(0)     // Dictionary { "a" -> 1; "b" -> 2 }
m.RowKV(0)   // [| KeyValuePair("a", 1); KeyValuePair("b", 2) |]
```

`AppendKV` preserves order on the wire; `Append(IReadOnlyDictionary)`
takes whatever order the dictionary yields.

### Tuple(T1, T2, …)

Heterogeneous, so `ColTuple` is non-generic — caller passes typed inner
columns by index and reads back through the same references.

```fsharp
let s   = ColStr()
let i64 = ColInt64()
let tup = ColTuple([| s :> IColumnResult; i64 :> IColumnResult |])

s.Append("alpha"); i64.Append(1L)
// after a SELECT into `tup` you read each inner directly:
s.Row(0); i64.Row(0)
```

`ColNamed<'T>(name, inner)` wraps a typed column to inject a field
name into the Tuple type string (`Tuple(name1 T1, name2 T2)`) without
changing the wire bytes:

```fsharp
let n1 = ColNamed<string>("user", ColStr())
let n2 = ColNamed<int64>("id", ColInt64())
ColTuple([| n1 :> IColumnResult; n2 :> IColumnResult |]).Type
// "Tuple(user String, id Int64)"
```

### LowCardinality(T)

`ColLowCardinality<'T>(inner)` — dictionary-coded with auto-sized keys
(1/2/4/8 bytes). `'T` must be `equality` + `not null`.

```fsharp
let lc = ColLowCardinality<string>(ColStr())
lc.Append("frequent")   // first occurrence: pushed to inner dict
lc.Append("frequent")   // dedup → same key
```

`LowCardinality(FixedString(N))` is not yet supported — `byte[]`
default equality is reference-based; a content-hash comparer is
needed.

---

## ColAuto — receive any scalar type

When the column type isn't known at compile time (ad-hoc SELECTs, type
explorers):

```fsharp
let auto = ColAuto()
// build a one-column SELECT where you don't know the type:
let q = { ChQuery.defaults with
            Body = "SELECT 42 :: Decimal(9, 2)"
            Results = [ { Name = ""; Column = auto } ]
            OnBlock = fun _ ->
                match auto.Inner with
                | Some (:? ColDecimal32 as d) -> printfn "%O" (Decimal.fromInt32 (d.Row 0) 2)
                | _ -> printfn "got %s" auto.Type }

client.DoAsync(q, ct).GetAwaiter().GetResult()
```

`ColAuto` handles every scalar — primitives, BFloat16, String, JSON,
Date/Date32/DateTime/DateTime64, UUID, IPv4, IPv6, Point, Nothing,
Decimal(P, S), Enum8/16, FixedString(N), Interval{Scale}. **Composites
are explicitly NOT auto-built** — use the explicit column class for
Array / Nullable / Map / Tuple / LowCardinality, since the inner
element type can't be erased without losing typed `Row` access.

The factory `ColAuto.build (typeString)` returns a configured
`IColumnResult` directly if you don't want the `ColAuto` wrapper.

---

## Compression

Pass `Compression = true` in `ChOptions` to negotiate LZ4 for the
session. The driver:

- decodes LZ4-compressed blocks on the receive path,
- encodes LZ4 frames for INSERT data blocks (with a method=None
  fallback when LZ4 would inflate),
- verifies the CityHash128 checksum on every received frame.

ZSTD is not yet supported (method byte 0x90 would route to a future
ZSTD decoder).

## Query settings

`ChQuery.Settings` is a list of `{ Key; Value; Important }` pairs sent
with the query packet. `Important = true` makes the server error on an
unknown key (otherwise it's silently ignored).

```fsharp
Settings = [ { Key = "max_block_size"; Value = "65536"; Important = true }
             { Key = "max_threads";    Value = "4";     Important = false } ]
```

## Type-string compatibility

The server's column type string doesn't always equal the client's
`Column.Type`. `Ch.Proto.ColumnType.normalize` runs on both sides
before the per-block type check:

- `Decimal(P, S)` → `Decimal32` / `Decimal64` / `Decimal128` / `Decimal256` (by P)
- `Enum8('a' = 1, …)` → `Enum8`, same for Enum16
- `DateTime('UTC')` → `DateTime`
- `DateTime64(3, 'UTC')` → `DateTime64(3)`

All other types match literally. Columns that implement `IInferable`
(`ColEnum8/16`, `ColDateTime64`, `ColInterval`, `ColAuto`) receive the
full server-sent type string before the compat check and self-configure.

---

## Repo layout

```
Ch.slnx                                .NET 10 solution
src/Ch.Proto/                          wire primitives + columns
  Buffer.fs / Reader.fs                 encode + decode primitives
  Varint.fs / Codes.fs                  LEB128, ClientCode/ServerCode
  CityHash128.fs                        ClickHouse's CH128 hash
  CompressedStream.fs                   LZ4 / None block framing
  Packets.fs                            ClientHello, ServerHello, Query, …
  Block.fs                              block-body decode
  Int256.fs                             Int256 / UInt256 32-byte structs
  Columns.fs                            IColumnResult / IColumnOf<'T> /
                                        IStatefulColumn / IInferable +
                                        ColPrimitive<'T> + 16 sealed leaves +
                                        ColumnType.normalize
  ColStr.fs                             variable-length String
  ColTime.fs                            Date / Date32 / DateTime / DateTime64 / IPv4
  ColFixed.fs                           FixedStr / UUID / IPv6
  ColLowCardinality.fs                  LowCardinality(T)
  ColNullable.fs                        Nullable(T)
  ColArr.fs                             Array(T)
  ColMap.fs                             Map(K, V)
  ColTuple.fs                           Tuple + ColNamed
  ColDecimal.fs                         Decimal32/64/128/256 + scaling helpers
  ColEnum.fs                            Enum8 / Enum16 + IInferable
  ColPoint.fs                           Point (Geo)
  ColNothing.fs                         Nothing
  ColInterval.fs                        Interval{Second..Year}
  ColJSONStr.fs                         JSON
  ColBFloat16.fs                        BFloat16
  ColAuto.fs                            scalar auto-dispatch
src/Ch.Client/                         connection lifecycle
  Options.fs                            ChOptions record
  Client.fs                             Connect / Ping / Do
src/Ch.Bench.Numbers/Program.fs        bench harness + --mixed / --insert smokes
tests/Ch.Proto.Tests/Col*Tests.fs      golden roundtrip + per-column tests
plans/
  DESIGN_CHOICES.md                     departures from ch-go
  HANDOVER.md                           session-boundary state doc
reference/ch-go/                       submodule (read-only)
```

## Performance notes

Decode hot path: one `ReadFull` per column body, byte buffer reused
across blocks, `MemoryMarshal.Cast<byte, 'T>` for zero-copy reinterpret.
No virtual dispatch on the bytes-in/bytes-out path — each
`ColPrimitive<'T>` is JIT-specialised per value type.

Self-DoS on `localhost:9000` (server + client share the same i7) makes
the canonical `SELECT 500M number FROM system.numbers_mt` bench
meaningless as a perf signal — see `RESULTS.md`. Where the bench
*is* measurable, F# is within ~5 % of ch-go on LZ4-compressed and
within ~10 % on uncompressed best-time.

---

## Status / coverage

Status of every column family and feature lives in
`plans/HANDOVER.md`. As of the latest commit: 200+ tests pass, INSERT
and SELECT both work end-to-end with and without LZ4 compression, and
ColAuto covers every scalar / parameterised type. Composites
(Array/Nullable/Map/Tuple/LC) are explicit-type only.

Reference implementation: [ClickHouse/ch-go](https://github.com/ClickHouse/ch-go).
Departures from ch-go are logged in `plans/DESIGN_CHOICES.md`.

## License

Apache License 2.0 — see [`LICENSE`](LICENSE). This project is a port of
[ch-go](https://github.com/ClickHouse/ch-go) by ClickHouse, Inc. and The Go
Faster Authors, also released under Apache 2.0. Attribution and the chain of
patent grants are preserved per the license terms — see [`NOTICE`](NOTICE).
Per-commit AI-assistant attribution is in the git history via
`Co-Authored-By` trailers.
