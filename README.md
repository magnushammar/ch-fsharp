# ch-fsharp

## The process of getting in range of ch-go

I am very comfortable with F# and like its ergonomics, and a couple of years ago I wrote a very feature-incomplete HTTP driver for ClickHouse to solve the problem of not finding a working .NET driver.

Fast forward to a couple of weeks ago, I decided to vibe port the C++ driver with Opus 4.7. It went quite well, but I also realized that I should have used the Go driver as a template.

So the last few days I let Opus port it with me as a cheerleader coming within some 20% of ch-go's performance on ch-bench. After pushing the model and digging deep for ideas, the driver itself ended up a touch faster than ch-go's on ch-bench — end-to-end it's within a couple percent, and best-run times tie. Something I assumed would be impossible, but here we are."

### Caveat about our "benching" and my perf numbers
When running ch-bench on my infra the driver isn't the bottleneck. system.numbers_mt generates rows slower than either client decodes them, so ~300 ms of the ~485 ms is the client just blocked in read(2) waiting on the server.

It feels really fast though ;)

## What role do I have then, if any?

It is still a tool. It's an amazing tool but it needs to be supplied with not only the high-level idea but also constant guidance. One of the biggest benefits is speed. I can ask for every crazy tweak and have it discarded or confirmed within minutes. Processing speed and coverage win over careful thought and analysis.
I am reduced to a manager with intuition about coding, and in this case I also got to learn more about the fundamentals of what makes code performant. I really like this new reality as I was never interested in programming as anything other than a tool to transform data into more useful forms. Now I can do that faster and better.

##  A low level .NET driver
This is a columnar protocol client, not an ADO.NET provider. There's no DbConnection/DbCommand/DbDataReader surface, no EF Core provider, and no row iterator — callers fill or drain IColumnOf<'T> instances directly. If you want row-at-a-time access or to plug into existing .NET data abstractions, wrap this driver behind your own adapter.

It's ported from
[ClickHouse/ch-go](https://github.com/ClickHouse/ch-go) (Apache 2.0, copyright
ClickHouse, Inc. and The Go Faster Authors). Performance > ergonomics. Native
TCP protocol (revision 54460), zero-copy column decode, LZ4 compression, INSERT
and SELECT. Build / test / run requires .NET 10 SDK and a reachable ClickHouse
server (default `localhost:9000`).

ch-go is included as a submodule.

```bash
dotnet run --project tests/Ch.Proto.Tests           # 233 Expecto tests
dotnet run --project src/Ch.Bench.Numbers -- --ping # smoke handshake
```

Published as two NuGet packages: **`ClickHouse.FSharp`** (wire protocol +
columnar codec) and **`ClickHouse.FSharp.Client`** (connection + query
lifecycle; depends on the first). The F# namespaces are unchanged — you
still `open Ch.Proto` and `open Ch.Client`.

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
use client = Client.Connect(opts, cts.Token)

client.Ping(cts.Token)
```

`Client` wraps one TCP connection. It is **not thread-safe** — use one
instance per concurrent query. Disposing the client closes the socket.

---

## Two fundamental usage patterns

The driver supports two coexisting paths, each optimised for a
different use case. Pick one per query; you can mix them per `Client`.

### Path 1 — Pre-declared typed columns (the fast path)

You know your schema at compile time. You declare typed columns
(`ColInt32()`, `ColStr()`, `ColArr<int32>(ColInt32())`, …), pass them
to a `SelectQuery.Results` / `InsertQuery.Input` list, and read /
write through the typed `Row(i) : 'T` / `Append(v: 'T)` surface. The
JIT specialises every primitive per value type — no virtual dispatch
on the bytes-in / bytes-out path. **Ship this in production.**

Minimal SELECT:

```fsharp
let n = ColInt32()
let s = ColStr()
let onBlock (rows: int) =
    for i in 0 .. rows - 1 do
        printfn "%d %s" (n.Row(i)) (s.Row(i))

let q = { SelectQuery.defaults with
            Body = "SELECT toInt32(number) AS n, toString(number) AS s
                    FROM system.numbers LIMIT 1000"
            Results = [ { Name = "n"; Column = n }
                        { Name = "s"; Column = s } ]
            OnBlock = onBlock }
client.Select(q, ct)
```

Minimal INSERT (against a pre-existing table):

```fsharp
let id    = ColInt32()
let name  = ColStr()
let score = ColFloat64()

id.Append(1);  name.Append("alpha"); score.Append(1.5)
id.Append(2);  name.Append("beta");  score.Append(2.5)

let q = { InsertQuery.defaults with
            Body  = "INSERT INTO my_table VALUES"
            Input = [ { Name = "id";    Column = id }
                      { Name = "name";  Column = name }
                      { Name = "score"; Column = score } ] }
client.Insert(q, ct)
```

Full details for every column type live under **Column types
reference** and **Composite types** below.

For zero-allocation bulk paths on the read/write hot loops — `AsSpan()`
on primitives, `AppendRange` for bulk write, `RowSpan(i)` on `ColArr`
when the inner is primitive, `ValueSpan()` / `NullsSpan()` on
`ColNullable`, `KeysSpan()` / `DictionarySpan()` on
`ColLowCardinality` — see the per-column sections and the
`IBulkAppendable<'T>` / `IBulkReadable<'T>` / `IRowBytes` facet
interfaces in `Columns.fs`.

### Path 2 — Runtime type-string dispatch via `ColAuto` (the generic path)

You don't know the schema at compile time — ad-hoc REPLs, schema
explorers, generic dashboards, type tooling. `ColAuto.build "Array(Int32)"`
parses the type string and returns the right concrete column.
Composites recurse (`Array(Nullable(LowCardinality(String)))` works);
`Map(K, V)` resolves via one-time reflection at construction
(`MakeGenericType` + `Activator.CreateInstance`), then the resulting
`ColMap<'K, 'V>` runs through the static JIT-specialised hot path
forever after.

```fsharp
// Build a column from a runtime type string:
let arr = ColAuto.build "Array(Nullable(Int32))" :?> ColArr<int32 voption>
let lc  = ColAuto.build "LowCardinality(String)"   :?> ColLowCardinality<string>
let map = ColAuto.build "Map(Int32, Array(Float64))" :?> ColMap<int32, float[]>

// Or use the ColAuto wrapper when you want lazy type inference inside
// a SELECT (the column resolves itself when the server sends the
// schema header):
let auto = ColAuto()
let q = { SelectQuery.defaults with
            Body = "SELECT 42 :: Decimal(9, 2) AS d"
            Results = [ { Name = "d"; Column = auto } ]
            OnBlock = fun _ ->
                match auto.Inner with
                | Some (:? ColDecimal32 as d) -> printfn "%O" (Decimal.fromInt32 (d.Row 0) 2)
                | _ -> printfn "got %s" auto.Type }
client.Select(q, ct)
```

`ColAuto.build` covers every column family this driver implements:
every primitive, Decimal(P,S), Enum8/16, DateTime64(N, ['tz']),
FixedString(N), Interval{Scale}, UUID, IPv4/6, Point, Nothing,
JSON, BFloat16 — and every composite: Array(T), Nullable(T),
LowCardinality(T), Tuple(...), Map(K, V).

The generic path's only extra cost (compared to Path 1) is one
`IColumnResult` virtual call per column per block on the
encode / decode side. For ad-hoc and tooling code that's invisible.
For the perf-critical inner loops of a real consumer, prefer Path 1.

### Both paths share the same Client

`client.Select(query, ct)` / `client.Insert(query, ct)` work identically
regardless of whether the columns in `Results` / `Input` were
pre-declared (Path 1) or built via `ColAuto.build` (Path 2). You can
mix — e.g. pre-declare the columns you know about and use `ColAuto`
for the ones you don't.

---

## Running queries

A query is either a `SelectQuery` or an `InsertQuery` record. Start from the
matching `defaults` and override only the fields you need:

```fsharp
{ SelectQuery.defaults with
    Body     = "SELECT ..."
    Results  = [...]            // caller's target columns
    OnBlock  = fun rows -> ()   // invoked per data block
    Settings = [...]            // query-scope key=value pairs
    QueryId  = None }           // optional UUID; auto-generated if None

{ InsertQuery.defaults with
    Body     = "INSERT INTO ... VALUES"
    Input    = [...]            // caller's source columns
    OnInput  = None             // optional multi-block streaming callback
    Settings = [...]
    QueryId  = None }
```

Pass a `SelectQuery` to `client.Select(query, ct)`, an `InsertQuery` to
`client.Insert(query, ct)` — each entry point accepts only its own record, so
SELECT/INSERT mix-ups are a compile error. DDL is a `SelectQuery` with an empty
`Results` (a statement that returns no rows). Calls are synchronous and block
the calling thread; `ct` is reserved (accepted, not yet honoured).

## SELECT — minimal

```fsharp
let n = ColInt32()
let s = ColStr()
let mutable totalRows = 0
let onBlock (rows: int) =
    for i in 0 .. rows - 1 do
        printfn "%d %s" (n.Row(i)) (s.Row(i))
        totalRows <- totalRows + 1

let q = { SelectQuery.defaults with
            Body = "SELECT toInt32(number) AS n, toString(number) AS s
                    FROM system.numbers LIMIT 1000"
            Results = [ { Name = "n"; Column = n }
                        { Name = "s"; Column = s } ]
            OnBlock = onBlock }

client.Select(q, ct)
```

Each `ColumnResult` has a `Name` (matched against the server's column
name; pass `""` to accept by index only) and a `Column` instance that
the driver decodes into in-place on every block. `OnBlock` runs after
all columns are filled.

> **Lifetime.** Decode is destructive — every block overwrites the previous
> block's values in the same buffer. The `Results` columns (and any
> `AsSpan()` view of them) are valid only *inside* `OnBlock`; copy out
> anything you need to keep past the callback.

## INSERT — minimal

```fsharp
let id    = ColInt32()
let name  = ColStr()
let score = ColFloat64()

id.Append(1);    name.Append("alpha"); score.Append(1.5)
id.Append(2);    name.Append("beta");  score.Append(2.5)

let q = { InsertQuery.defaults with
            Body = "INSERT INTO my_table VALUES"
            Input = [ { Name = "id";    Column = id }
                      { Name = "name";  Column = name }
                      { Name = "score"; Column = score } ] }

client.Insert(q, ct)
```

### Multi-block streaming insert

For sources too large to hold in memory, set `OnInput = Some next`. The
key thing to internalise — and the bit that's easy to miss from the
function signature alone — is **when** `next` fires:

1. Caller pre-populates the Input columns *before* `Insert`.
2. `Insert` sends the query and receives the server's schema header
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

let q = { InsertQuery.defaults with
            Body    = "INSERT INTO my_table VALUES"
            Input   = [ { Name = "id";    Column = id }
                        { Name = "value"; Column = value } ]
            OnInput = Some onInput }

client.Insert(q, ct)
```

Common slip: starting with empty columns and expecting `next` to fill
the first batch. It won't — `next` only fires *after* a block has been
sent, so block #1 would be empty. Always pre-fill before `Insert`.

**Without OnInput** the contents of the Input columns at `Insert` time
are sent as a single block. Use this for fixed-size payloads that
already fit in memory.

**OnBlock vs OnInput.** `OnBlock` belongs to `SelectQuery` — it fires for
every server `Data` block during a `Select`, after all decode targets in
`Results` are filled. `InsertQuery` has no `OnBlock`: the server's only
`Data` packet for an INSERT is the schema header (rows=0), which the driver
consumes internally to infer Input column types.

> ⚠ **Don't SELECT immediately after an INSERT in the same session.**
> ClickHouse doesn't promise read-your-own-writes inside a session,
> and real ClickHouse usage never reaches for that pattern in the
> first place — the DB is append-mostly, aggregate-later, and its
> design comes from being a logging database for a search engine. If
> your application needs immediate read-after-write of the data it
> just inserted, you're using the wrong database; ClickHouse is not
> Postgres and won't pretend to be. Verify writes out-of-band via
> `clickhouse-client` or a separate, later connection.

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

`LowCardinality(FixedString(N))` is supported via
`ColLowCardinality<byte array>(ColFixedStr(N))`. The dedup dictionary
auto-defaults to `ByteArrayContentEqualityComparer` (FNV-1a content
hash) for `byte[]` keys, since default array equality is
reference-based. Pass a custom `IEqualityComparer<byte[]>` to the
constructor if you want different semantics.

```fsharp
let lc = ColLowCardinality<byte array>(ColFixedStr(8))
lc.Append(Array.replicate 8 0xAAuy)
lc.Append(Array.replicate 8 0xAAuy)  // dedup → reference-distinct, content-same
// lc.Inner.Rows = 1; lc.Rows = 2
```

---

## ColAuto — receive any scalar type

When the column type isn't known at compile time (ad-hoc SELECTs, type
explorers):

```fsharp
let auto = ColAuto()
// build a one-column SELECT where you don't know the type:
let q = { SelectQuery.defaults with
            Body = "SELECT 42 :: Decimal(9, 2)"
            Results = [ { Name = ""; Column = auto } ]
            OnBlock = fun _ ->
                match auto.Inner with
                | Some (:? ColDecimal32 as d) -> printfn "%O" (Decimal.fromInt32 (d.Row 0) 2)
                | _ -> printfn "got %s" auto.Type }

client.Select(q, ct)
```

`ColAuto` handles every scalar — primitives, BFloat16, String, JSON,
Date/Date32/DateTime/DateTime64, UUID, IPv4, IPv6, Point, Nothing,
Decimal(P, S), Enum8/16, FixedString(N), Interval{Scale} — **plus
every composite**: `Array(T)`, `Nullable(T)`, `LowCardinality(T)`,
`Tuple(...)` recursively, and `Map(K, V)` (general K/V via one-time
reflection at construction, with `Map(String, String)` on a static
fast path). The resulting column runs through the JIT-specialised
hot path forever after — reflection is paid once, decode / encode
never.

```fsharp
let arr  = ColAuto.build "Array(Nullable(Int32))"   // ColArr<int32 voption>
let lc   = ColAuto.build "LowCardinality(String)"   // ColLowCardinality<string>
let lcfs = ColAuto.build "LowCardinality(FixedString(8))"  // ColLowCardinality<byte[]>
let map  = ColAuto.build "Map(Int32, Array(Float64))"      // ColMap<int32, float[]>
```

To use a typed column, downcast the result. The factory
`ColAuto.build (typeString)` returns the `IColumnResult` directly if
you don't want the `ColAuto` wrapper.

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

Both `SelectQuery` and `InsertQuery` carry a `Settings` list of
`{ Key; Value; Important }` pairs sent with the query packet.
`Important = true` makes the server error on an unknown key (otherwise
it's silently ignored).

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
  BlockingFdStream.fs                   blocking read(2)/write(2) on the raw fd
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
  Client.fs                             Connect / Ping / Select / Insert
src/Ch.Bench.Numbers/Program.fs        bench harness + --mixed / --insert smokes
tests/Ch.Proto.Tests/Col*Tests.fs      golden roundtrip + per-column tests
plans/
  DESIGN_CHOICES.md                     departures from ch-go
  HANDOVER.md                           session-boundary state doc
  PERF_INVESTIGATION.md                 full perf investigation log
bench/fs, bench/go                     minimal ch-bench harnesses (A/B)
scripts/                               bench + A/B measurement runners
reference/ch-go/                       submodule (read-only)
```

## Performance notes

Decode hot path: one `ReadFull` per column body, byte buffer reused
across blocks, `MemoryMarshal.Cast<byte, 'T>` for zero-copy reinterpret.
No virtual dispatch on the bytes-in/bytes-out path — each
`ColPrimitive<'T>` is JIT-specialised per value type. The `Reader` owns
its read buffer (no `BufferedStream` layer): header bytes — varints,
fixed-size ints — are served by index, not a virtual `Stream.Read` per
byte.

Benchmarked against ch-go on the canonical `SELECT number FROM
system.numbers_mt LIMIT 500000000` (3.73 GiB of UInt64), both clients
pinned to the P-cores, 50-run alternating protocol — full method and
evidence in [`plans/PERF_INVESTIGATION.md`](plans/PERF_INVESTIGATION.md).
**The F# driver runs ~25–35 ms faster than ch-go's driver. On my machine™.** The
headline wall time sits ~2 % behind ch-go, but that gap is the bench's
client-side `sum` scaffolding (not driver code), and the workload is
server-production-bound — the client is faster than ClickHouse can feed
it, so neither client's wall time is a pure driver measurement. Best-run
times effectively tie.

---

## Status / coverage

Status of every column family and feature lives in
`plans/HANDOVER.md`. As of v0.4.0: 233 tests pass, INSERT and SELECT
both work end-to-end with and without LZ4 compression, and `ColAuto`
covers every column we implement — scalars, parameterised types,
*and* composites (Array / Nullable / Map / Tuple / LowCardinality
recursively, plus general `Map(K, V)` via one-time reflection at
construction).

Reference implementation: [ClickHouse/ch-go](https://github.com/ClickHouse/ch-go).
Departures from ch-go are logged in `plans/DESIGN_CHOICES.md`.

## License

Apache License 2.0 — see [`LICENSE`](LICENSE). This project is a port of
[ch-go](https://github.com/ClickHouse/ch-go) by ClickHouse, Inc. and The Go
Faster Authors, also released under Apache 2.0. Attribution and the chain of
patent grants are preserved per the license terms — see [`NOTICE`](NOTICE).
Per-commit AI-assistant attribution is in the git history via
`Co-Authored-By` trailers.
