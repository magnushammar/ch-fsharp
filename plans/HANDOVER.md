# Handover

Resumption point for a fresh session. Read this + `plans/DESIGN_CHOICES.md`
+ skim recent `git log --oneline` and you should be productive within a few
minutes.

## What this project is

F# port of [ClickHouse/ch-go](https://github.com/ClickHouse/ch-go) — a
low-level columnar TCP client. Reference submodule at `reference/ch-go/`.

Three API layers planned; **we are building only the lowest one** right
now. The other two (row-based .NET idiomatic; CE-based) will wrap the
low-level layer later, and we're not concerned with them yet.

Performance > ergonomics. Match ch-go's decisions unless we can clearly
improve them (and log departures in DESIGN_CHOICES.md when we do).

## Where we are

64 commits in. Recent run added:

```
c042eaf  Extend --mixed bench to JSON column with required server settings
a61c464  Add ColJSONStr: String wire + UInt64 serialization-version state header
7b54ac0  Extend --mixed bench to IntervalDay column
5ad6ac8  Add ColInterval: Int64 wire + 8-scale IntervalScale tag
7b02f3e  Extend --mixed bench to Point column
e1dc7c5  Add ColPoint (Geo) and ColNothing (Nullable(Nothing))
a5b5165  Extend --mixed bench to Enum8 column
792a808  Add ColEnum8/16 + IInferable; normalize strips Enum parameters
d6c113d  Extend --mixed bench to Decimal(9, 2)
6d36090  Add ColDecimal32/64/128 + ColumnType.normalize for Decimal(P, S)
ed65950  Extend --mixed bench smoke to Tuple(String, Int32) column
8018be5  Add ColTuple + ColNamed: heterogeneous columns side by side
085a6b4  Extend --mixed bench smoke to Map(String, String) column
035b8d8  Add ColMap<'K, 'V>: Offsets + parallel keys/values inner columns
```

**160/160 tests pass.** Live multi-type SELECT works end-to-end via
`/tmp/ch-bench-fs/Ch.Bench.Numbers --mixed` against `localhost:9000`,
exercising 12 result columns including Map, Tuple, Decimal, Enum8,
Point, IntervalDay and JSON.

## Repo map

```
Ch.slnx                                  .NET 10 solution (xml format)
Directory.Build.props                    TargetFramework=net10.0, Nullable=enable
src/Ch.Proto/                            wire primitives + columns
  Buffer.fs / Reader.fs                   encode + decode primitives
  Varint.fs / Codes.fs                    LEB128, ClientCode/ServerCode
  CityHash128.fs                          ClickHouse's CH128 hash
  CompressedStream.fs                     LZ4/None block framing + CompressedFrame writer
  Packets.fs                              ClientHello, ServerHello, Query, …
  Block.fs                                Block-body decode (after state header)
  Columns.fs                              IColumnResult/IColumnOf/IStatefulColumn/
                                          IInferable + ColumnType.normalize +
                                          ColPrimitive<'T> abstract + 11 sealed leaves
  ColStr.fs                               variable-length String column
  ColTime.fs                              Date / Date32 / DateTime / DateTime64 / IPv4
  ColFixed.fs                             ColFixedBytes abstract + FixedStr / UUID / IPv6
  ColLowCardinality.fs                    LowCardinality(T)
  ColNullable.fs                          Nullable(T)
  ColArr.fs                               Array(T) + recursive
  ColMap.fs                               Map(K, V)
  ColTuple.fs                             Tuple(...) + ColNamed
  ColDecimal.fs                           Decimal32/64/128 + Decimal scaling helpers
  ColEnum.fs                              Enum8 / Enum16 with name<=>int map
  ColPoint.fs                             Point (Geo)
  ColNothing.fs                           Nothing (for Nullable(Nothing))
  ColInterval.fs                          Interval{Second..Year}
  ColJSONStr.fs                           JSON (String wire + version state header)
src/Ch.Client/                           connection lifecycle
  Options.fs                              ChOptions record
  Client.fs                               Connect/Ping/Do, state dispatch
src/Ch.Bench.Numbers/Program.fs          bench harness + --mixed smoke
tests/Ch.Proto.Tests/Col*Tests.fs        per-column tests with golden roundtrip
plans/
  DESIGN_CHOICES.md                       running log of ch-go departures
  HANDOVER.md                             this file
bench/go/main.go                         Go wrapper of ch-go for comparison bench
scripts/bench.py                         hyperfine-shaped Python bench runner
reference/ch-go/                         submodule (read-only)
RESULTS.md                               bench numbers + analysis
```

## Coverage status

✅ done · ⛔ deferred · 🔜 next

| Family | Status |
|---|---|
| Integers (8/16/32/64, signed+unsigned) | ✅ |
| Integers wide (Int128, UInt128) | ✅ |
| Floats (Float32, Float64) | ✅ |
| Bool | ✅ |
| String, FixedString(N) | ✅ |
| Date, Date32, DateTime, DateTime64(N) | ✅ |
| IPv4, IPv6 | ✅ |
| UUID | ✅ |
| LowCardinality(T) — String + numerics | ✅ |
| LZ4 compression + CityHash128 | ✅ |
| Nullable(T) | ✅ |
| Array(T) (recursive) | ✅ |
| Map(K, V) | ✅ |
| Tuple(T1, …, Tn) + ColNamed | ✅ |
| Decimal32/64/128 | ✅ |
| Enum8 / Enum16 (+ IInferable) | ✅ |
| Point (Geo) | ✅ |
| Nothing / Nullable(Nothing) | ✅ |
| Interval{Second..Year} | ✅ |
| JSON (string serialization v1) | ✅ |
| Decimal256 | ⛔ (no Int256 yet) |
| Int256, UInt256 | ⛔ (no .NET native) |
| BFloat16 | ⛔ |
| Time / Time64 | ⛔ (server here doesn't have it) |
| LowCardinality(FixedString) | ⛔ (needs content-hash IEqualityComparer) |
| ColAuto inference | ⛔ |
| INSERT / OnInput streaming | 🔜 (biggest remaining surface) |
| Full-duplex query loop | 🔜 (needed for INSERT) |
| Connection pool | ⛔ (ch-go has it as separate package) |
| SSH auth, OTel, query parameters | ⛔ |
| ZSTD compression | ⛔ (LZ4 covers dominant case) |
| Query cancellation watchdog | ⛔ |

## Next milestones (in suggested order)

1. **M-INSERT**: substantial — needs the full-duplex send/recv pattern
   from `query.go: Do`. The receiver collects column type info from the
   server's header block and hands it to the sender via a channel so the
   sender can do type inference on input columns before encoding rows.
   Once this lands, all the columns we have are usable on the write side.
   ch-go reference: `query.go`, `client.go: sendQuery/sendData`.

2. **Int256 / UInt256** (then Decimal256): needs a 32-byte struct in F#
   with the right `MemoryMarshal` layout. Without `System.Int256`, easiest
   path is an explicit struct of 4 × `UInt64` little-endian + arithmetic
   helpers as needed. Read/write are still single `ReadFull`/`PutRaw`.

3. **LowCardinality(FixedString)**: needs an `IEqualityComparer<byte[]>`
   that hashes content (FNV-1a or xxhash). ColLowCardinality's dedup
   Dictionary currently uses default reference equality, which would
   never deduplicate byte arrays.

4. **ColAuto inference**: routes the server-sent type string through a
   factory that builds the right concrete column. Useful as a high-level
   convenience; not perf-critical. ch-go reference: `proto/col_auto.go`.

5. **BFloat16**: needs a custom 16-bit struct with the BFloat16 bit layout
   (1 sign + 8 exponent + 7 mantissa, distinct from .NET `Half`). ch-go
   reference: `proto/col_bfloat16.go`.

After **M-INSERT** the column suite covers ch-go's actually-supported
set bidirectionally. The deferred entries are increasingly niche.

## Conventions to follow

- **One column type per file** in `src/Ch.Proto/Col*.fs`. Group close cousins
  (e.g. ColTime.fs has Date/Date32/DateTime/DateTime64/IPv4).
- **Tests per file** in `tests/Ch.Proto.Tests/Col*Tests.fs`. Use the
  `goldenPath` helper (climbs from `AppContext.BaseDirectory` to the submodule
  fixture). xunit, `[<Theory>]` + `[<InlineData>]` for parameterized cases.
- **New `.fs` files must be added** to `src/Ch.Proto/Ch.Proto.fsproj` (or the
  test fsproj). F# compile order matters — add it after its dependencies.
- **Every column implements `IColumnResult`** (Type, Rows, Reset,
  Decode/EncodeColumn). For columns with a typed value type, also
  `IColumnOf<'T>` (Append+Row). For columns with a per-block state header
  (LowCardinality, JSON, Tuple-of-stateful), `IStatefulColumn`. For
  columns whose layout/scale/map depends on the server-sent parameterised
  type string (Enum, Interval, future DateTime tz), `IInferable`.
- **Server-sent type strings may differ from the client's `Type`.** The
  `ColumnType.normalize` regex in `Columns.fs` folds `Decimal(P, S)` →
  `Decimal{32,64,128,256}` and `Enum8(…)` / `Enum16(…)` → bare form.
  Extend that table when adding a new parameterised column.
- **No `not null` unless the type actually requires it** — `not null` propagates
  to callers via F# Nullable=enable. Only LC needs it (Dictionary key).
- **Buffer growth must preserve contents** — use `Array.Resize(&buf, newCap)`,
  never `Array.zeroCreate` for grow. This bit us once (see commit `c9d66d6`).
- **Generic writes via `MemoryMarshal.Write<'T>`** — `Span<'T>.[0] <- v` does
  not always persist for generic 'T (see same commit).
- **Skip state header for empty blocks** — server omits state when rowCount=0.
  `Client.fs` handles this; new stateful columns should not assume state is
  always present.

## Live test environment

ClickHouse server running on `localhost:9000`, password in env var
`CLICKHOUSE_PASSWORD` (= `changeme` on the dev box). Server revision 54483.

Smoke test the driver:
```bash
dotnet publish src/Ch.Bench.Numbers -c Release -o /tmp/ch-bench-fs
/tmp/ch-bench-fs/Ch.Bench.Numbers --mixed   # multi-type SELECT
/tmp/ch-bench-fs/Ch.Bench.Numbers --rows 100  # tiny UInt64 SELECT
/tmp/ch-bench-fs/Ch.Bench.Numbers --ping    # handshake only
```

Run all tests: `dotnet test Ch.slnx`.

Manual server queries: `clickhouse-client --password "$CLICKHOUSE_PASSWORD"
-q "..."`. Useful for inspecting golden bytes, profile events, etc.

## Bench takeaways (RESULTS.md has detail)

The bench is **not useful as a perf signal on this hardware** — server and
client share a consumer i7, so we measure CPU contention + power-state
recovery, not driver throughput.

ch-bench's published numbers come from `hyperfine -w 10 -r 100` (no settle)
on a setup where this self-DoS effect doesn't dominate. We can't reproduce
that locally. RESULTS.md documents the bimodal pattern (fast ~600 ms ↔
slow ~1900 ms) that affects all three clients (F#, Go, clickhouse-client
uncompressed) symmetrically.

We are at perf parity with ch-go where it can be measured (within ~5% on
LZ4-compressed, within ~10% on uncompressed best-time).

## Gotchas / non-obvious things

1. **`Buffer` is renamed `Buf`** because `System.Buffer` (static BCL class)
   shadows it once you `open System`.
2. **Build server can crash** on Linux .NET 10 occasionally with
   "Fatal error. Internal CLR error. (0x80131506)". Recover with
   `dotnet build-server shutdown && pkill -f MSBuild` then rebuild.
3. **`/tmp/ch-bench-fs/Ch.Bench.Numbers` paths**: I keep publishing here
   for convenience. Switch to a project-local path if it bothers you.
4. **`reference/ch-go/proto/_golden/` filenames**: not always a clean mapping
   to F# test names. `col_str.raw`, `col_str_bytes.raw`, `col_arr_int8_manual.raw`,
   `col_low_cardinality_i_str_k_8.raw`. Read the ch-go test before writing F#.
5. **CLR generic specialisation per value type** is doing a lot of work for us.
   `ColPrimitive<int32>` is a separate JIT'd code path from
   `ColPrimitive<uint64>`. Non-virtual calls, no boxing. F# inheritance from a
   generic abstract is the cheapest way to express it.
6. **F# Nullable=enable is strict**: `null` literal assignment requires
   `| null` annotation. Avoid by initializing eagerly (e.g. ColLowCardinality's
   `values` and `dedup`) or using `voption`.
7. **The `--mixed` bench flag** is the canonical end-to-end smoke. Update it
   when adding a new column type so we always have a real-server multi-type
   exercise.

## Open questions / things I'd want to revisit

- LC dictionary cross-block caching (currently re-allocates dict strings per
  block — could keep an in-column `Dictionary<'T,int>` across blocks for the
  same `inner.Pos` window).
- Whether to expose a `Span<byte>`-only fast path on `ColStr.Row` to avoid
  the UTF-8 decode allocation. ch-go has `RowBytes(i)` for this.
- Decimal: 96-bit `System.Decimal` doesn't fit Decimal128/256 cleanly.
  Could use a separate `BigDecimal` / `System.Numerics.BigInteger`-based
  wrapper, or just expose raw Int128/256.
- INSERT design: ch-go uses a `Preparable` interface invoked before encode;
  we fold it into `EncodeColumn`. For INSERT we'll need the column to
  report its row count cheaply — already have `Rows`, should be fine.

## How to resume

1. Read this file.
2. Skim `plans/DESIGN_CHOICES.md`.
3. `git log --oneline -10` and `git status`.
4. `dotnet test Ch.slnx` (should be 82/82).
5. `/tmp/ch-bench-fs/Ch.Bench.Numbers --mixed` (should print 6 rows of
   mixed-type data; if /tmp is gone, republish first).
6. Pick the next milestone from the list above. M-Map is the smallest.
