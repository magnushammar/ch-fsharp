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

The column suite covers ch-go's supported set bidirectionally; INSERT and
SELECT both work end-to-end with and without LZ4. `git log --oneline` has
the commit-level detail.

**Tier 1 (API-surface fixes) shipped — v0.3.0.** The client API is now
honestly synchronous: `Client.Connect` / `.Ping` / `.Select` / `.Insert`
return plain values, no `Task` wrappers. SELECT vs INSERT is type-safe —
`SelectQuery` and `InsertQuery` are separate records dispatched by
separate methods, replacing the old mode-inferred `ChQuery`
(`DESIGN_CHOICES.md` items 12–13).

**Tier 2 (ColAuto composites via the ColPrimitive collapse) shipped.**
`ColAuto.build` now constructs `Array(T)` / `Nullable(T)` /
`LowCardinality(T)` / `Tuple(...)` recursively via three facet
interfaces — `IArrayable`, `INullable`, `ILowCardinality` — implemented
on the `ColPrimitive<'T>` base (every primitive leaf inherits for
free) plus on every typed non-primitive column. `ColArr` /
`ColNullable` / `ColMap` internals refactored to raw `uint64[]` /
`byte[]` / `uint64[]` to break the previous ColPrimitive → ColArr →
ColUInt64 cycle. Wire format bit-identical.

**Tier 3 (bulk-access facets + LC(String) byte-span + LC(FixedString)
+ Map(K,V) in ColAuto) shipped.** Three more additive facets in
`Columns.fs` — `IBulkAppendable<'T>` (`AppendRange`), `IBulkReadable<'T>`
(`AsSpan`), `IRowBytes` (`RowBytes`) — wired on the appropriate
columns. `ColArr<'T>.AppendSpan` and the new `RowSpan(i)` dispatch via
these for zero-alloc bulk paths when `inner` is primitive.
`ColNullable<'T>.ValueSpan()` exposes the typed values span for
branchless null-masking. `ColLowCardinality<'T>` now does inline
dedup on `Append` (no staged-values buffer) and materialises the
`'T[]` dict lazily on first `Row(i)` — byte-span consumers using
`RowSpan(i)` skip materialisation entirely. `LowCardinality(FixedString(N))`
works end-to-end via `ByteArrayContentEqualityComparer` (FNV-1a)
plus `ColFixedStr : IColumnOf<byte[]>` + the three composite facets.
`ColAuto.build` general `Map(K, V)` via one-time reflection at
construction (`Map(String, String)` stays on a static fast path).

All Expecto tests pass. Live smokes against `localhost:9000` —
`Ch.Bench.Numbers --ping / --mixed / --rows 100` — all green:
`--mixed` exercises 14 result columns through Map, Tuple, Decimal,
Enum8, Point, IntervalDay, JSON, BFloat16, and
`LowCardinality(FixedString(8))`. `ColAuto` resolves any scalar /
parameterised / composite (Array / Nullable / LC / Tuple / Map) type
from a server-sent type string for ad-hoc receive.

> `--insert` currently flakes on the pre-existing Atomic-DB visibility
> race documented under "Gotchas" — not a driver regression; CREATE
> TABLE returns before the table is visible to the subsequent INSERT.
> See "Next milestones" for the planned mitigation.

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
  ColBFloat16.fs                          BFloat16 (UInt16 wire + float32 helpers)
  ColDecimal.fs                           Decimal{32,64,128,256} + scaling helpers
  Int256.fs                               Int256 / UInt256 32-byte structs
src/Ch.Client/                           connection lifecycle
  Options.fs                              ChOptions record
  Client.fs                               Connect/Ping/Select/Insert, state dispatch
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
| Decimal32/64/128/256 | ✅ |
| Enum8 / Enum16 (+ IInferable) | ✅ |
| Point (Geo) | ✅ |
| Nothing / Nullable(Nothing) | ✅ |
| Interval{Second..Year} | ✅ |
| JSON (string serialization v1) | ✅ |
| Int256, UInt256 (custom 32-byte struct) | ✅ |
| BFloat16 (float32 helpers) | ✅ |
| LZ4 framing on INSERT bodies | ✅ |
| ColAuto inference (scalars + parameterised + composites + Map(K,V)) | ✅ |
| Bulk-access facets (IBulkAppendable / IBulkReadable / IRowBytes) | ✅ |
| LowCardinality(String) byte-span access (RowSpan + lazy dict) | ✅ |
| LowCardinality(FixedString(N)) — content-hash dedup | ✅ |
| Time / Time64 | ⛔ (server here doesn't have it) |
| INSERT (single block + OnInput streaming) | ✅ |
| Full-duplex query loop | ✅ (single-threaded send/wait-header/send-input/drain) |
| Connection pool | ⛔ (ch-go has it as separate package) |
| SSH auth, OTel, query parameters | ⛔ |
| ZSTD compression | ⛔ (LZ4 covers dominant case) |
| Query cancellation watchdog | ⛔ |

## Next milestones (in suggested order)

1. **Atomic-DB INSERT visibility race**: `Ch.Bench.Numbers --insert`
   flakes because the default `default` database is an Atomic engine,
   where `CREATE TABLE` returns before the table is visible. The
   200 ms sleep in the smoke isn't enough. Real fix options: query
   `system.tables` until the table appears, switch the smoke to a
   non-Atomic engine, or split the smoke into create-and-poll +
   insert-and-verify halves. Pure tooling change; no driver code
   touched.

2. **Connection pool**: ch-go has it in a separate `chpool` package and
   explicitly disclaims it as out-of-core. Worth a small F# port for
   server workloads but not perf-critical for the driver itself.

3. **Query cancellation watchdog**: a `Task`-side fiber that fires
   `ClientCodeCancel` when the user's `CancellationToken` flips. Today
   we plumb the `CancellationToken` through but never proactively send
   Cancel.

4. **ZSTD compression**: LZ4 covers the dominant case. ZSTD adds another
   compression library dependency and a method=0x90 encode/decode branch
   parallel to the existing LZ4 path.

The column suite already covers ch-go's supported set bidirectionally
(read + write) including LZ4 framing on both sides.

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
/tmp/ch-bench-fs/Ch.Bench.Numbers --insert  # CREATE + INSERT + SELECT round-trip
/tmp/ch-bench-fs/Ch.Bench.Numbers --rows 100  # tiny UInt64 SELECT
/tmp/ch-bench-fs/Ch.Bench.Numbers --ping    # handshake only
```

Run all tests: `dotnet run --project tests/Ch.Proto.Tests`.

Manual server queries: `clickhouse-client --password "$CLICKHOUSE_PASSWORD"
-q "..."`. Useful for inspecting golden bytes, profile events, etc.

## Bench takeaways (PERF_INVESTIGATION.md has detail)

The bench is **not useful as a perf signal on this hardware** — server and
client share a consumer i7, so we measure CPU contention + power-state
recovery, not driver throughput.

ch-bench's published numbers come from `hyperfine -w 10 -r 100` (no settle)
on a setup where this self-DoS effect doesn't dominate. We can't reproduce
that locally. `PERF_INVESTIGATION.md` documents the full investigation,
including the bimodal pattern (fast ~600 ms ↔ slow ~1900 ms) that affects
all three clients (F#, Go, clickhouse-client uncompressed) symmetrically.

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
4. `dotnet run --project tests/Ch.Proto.Tests` (all tests should pass).
5. `/tmp/ch-bench-fs/Ch.Bench.Numbers --mixed` (should print 6 rows of
   mixed-type data; if /tmp is gone, republish first).
6. Pick the next milestone from the list above. M-Map is the smallest.
