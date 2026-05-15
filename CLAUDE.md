# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project identity

This is a **low-level columnar ClickHouse driver**, ported from
[ClickHouse/ch-go](https://github.com/ClickHouse/ch-go) (Apache 2.0).
The repo's canonical name is `magnushammar/ch-fsharp`; `ch-go-fsharp` and
`latinum` are dead names from earlier iterations — do not reintroduce them
in code, packages, or docs.

**Driver philosophy: speed > ergonomics, anti-convention.** This is *not*
an ADO.NET provider — no `DbConnection` / `DbCommand` / `DbDataReader`,
no EF Core, no row iterator. Callers fill / drain `IColumnOf<'T>` directly.
Do not "fix" the API to chase System.Data conventions; that's an explicit
non-goal. Name clashes with consumer code (e.g. `Client`) are the
consumer's to qualify around.

Reference implementation lives at `reference/ch-go/` (git submodule,
read-only). When in doubt about wire format or semantics, check the
Go source there before guessing.

## Cardinal rule: chase every improvement

If you notice a hint of a possible optimization or API improvement that
does not hurt performance, **investigate it thoroughly and implement it
— even when the win looks small.** We aim for perfection and are
willing to pay the price of refactors, breaking changes, and careful
review to get there. This is pre-1.0; the cost of code churn is low and
the cost of accumulated "good enough" is high.

In practice:

- Don't park observations like "this could be cleaner / smaller /
  faster" for later. Surface them, investigate, implement.
- Don't dismiss a fix because the diff would touch many files — we
  have no external consumers (see "Pre-1.0 API stability" below).
- The opposite failure is also a failure: shipping an "improvement"
  that silently regresses perf, type fidelity, or wire correctness.
  Changes that touch the encode / decode hot path or wire format
  require benchmark + golden-byte verification before they land (see
  "Triaging proposed changes" below).

## Commands

All commands assume the repo root as cwd. .NET 10 SDK and a reachable
ClickHouse server (`localhost:9000`, password in `$CLICKHOUSE_PASSWORD`)
are required for the bench / smoke harnesses; only the build and test
commands work offline.

```bash
dotnet build Ch.slnx                                   # build whole solution
dotnet run --project tests/Ch.Proto.Tests              # run all Expecto tests
dotnet run --project tests/Ch.Proto.Tests -- --summary # tests with summary line
dotnet run --project tests/Ch.Proto.Tests -- --filter-test-list ColArr  # single suite
dotnet run --project tests/Ch.Proto.Tests -- --filter-test-case "foo"   # single case

# Live-server smokes (Ch.Bench.Numbers reads $CLICKHOUSE_PASSWORD from env)
dotnet run --project src/Ch.Bench.Numbers -c Release -- --ping     # handshake only
dotnet run --project src/Ch.Bench.Numbers -c Release -- --mixed    # 13-column end-to-end
dotnet run --project src/Ch.Bench.Numbers -c Release -- --insert   # CREATE+INSERT+SELECT
dotnet run --project src/Ch.Bench.Numbers -c Release -- --rows 100 # tiny UInt64 SELECT
```

**Test runner is Expecto.** Always invoke via `dotnet run` — `dotnet test`
does not surface Expecto's filtering or output correctly.

**`--mixed` is the canonical end-to-end smoke.** It exercises every
composite (Array, Nullable, Map, Tuple, LowCardinality) plus 13 column
families on the receive path. Update its SELECT when adding a new
column type so the round-trip stays covered.

`scripts/bench.py` is a Python hyperfine-clone for A/B perf runs against
`bench/go/` (ch-go wrapper). Not needed for routine development.

## Layout (one-line orientation)

```
src/Ch.Proto/      wire primitives + column codec (one type per Col*.fs)
src/Ch.Client/     connection lifecycle: Client.Connect / Ping / Select / Insert
src/Ch.Bench.Numbers/  bench harness; --mixed / --insert smokes live here
tests/Ch.Proto.Tests/  Expecto, one Col*Tests.fs per column
reference/ch-go/   submodule — read-only Go reference implementation
plans/             HANDOVER.md, DESIGN_CHOICES.md, PERF_INVESTIGATION.md
bench/fs, bench/go minimal ch-bench harnesses for cross-language A/B
```

Packaged as two NuGet projects: `ClickHouse.FSharp` (Ch.Proto) and
`ClickHouse.FSharp.Client` (Ch.Client). Version comes from
`Directory.Build.props`.

## Architecture (the big picture)

### Decode pipeline (SELECT)

`Client.Select` sends the Query packet, then enters a receive loop:
`Reader.fs` reads framed packets from a `BlockingFdStream` (raw `read(2)`
on the socket fd — bypasses .NET's `SocketAsyncEngine` to avoid epoll
busy-loop on the receive path). For each `Data` packet, `Block.fs`
parses the block header, calls `Infer` on any `IInferable` Results column
(Enum maps, DateTime64 precision, etc.), runs `ColumnType.normalize`
to fold parameterized types (`Decimal(P,S)` → `Decimal{32,64,128,256}`,
`Enum8('a'=1,...)` → `Enum8`, `DateTime('UTC')` → `DateTime`), then
hands each column body to its `DecodeColumn(reader, rowCount)`. After
all columns are filled, `OnBlock(rows)` fires.

**Decode is destructive — every block overwrites the previous block's
buffers in place.** Results columns (and any `AsSpan()` view) are
valid only *inside* `OnBlock`; copy out anything you need to keep.

### Encode pipeline (INSERT)

`Client.Insert` sends the Query packet plus a blank external-data
marker, then waits for the server's first `Data` packet — which has
`rows=0, columns=N` (the schema header). Each Input column's `Infer`
runs against the server-supplied type string, then the driver encodes
the caller's pre-filled columns as data block #1, calls `OnInput()` if
present to fetch block #2, …, finally writes a blank end-of-data
block and drains Progress / Profile / EndOfStream. The send → wait-header
→ send-input → drain loop is single-threaded by design (ch-go uses a
goroutine + channel; we don't need to).

**Critical INSERT contract: `OnInput` fires *after* each block is on
the wire, not before.** Pre-fill Input columns before calling `Insert`
for block #1. Inside `OnInput`, reset the columns then refill them for
the next block (the driver does *not* reset Input columns for you).

### Column type system

`IColumnResult` is the universal column interface (Type / Rows / Reset
/ DecodeColumn / EncodeColumn). Typed columns also implement
`IColumnOf<'T>` for typed `Append` / `Row`. Columns with a per-block
state header (LowCardinality, JSON, Tuple-of-stateful) implement
`IStatefulColumn`. Columns whose layout / mapping depends on the
server-sent parameterized type string (Enum, Interval, DateTime64, ColAuto)
implement `IInferable`.

**The 16 primitive leaves are sealed subclasses of a generic
`ColPrimitive<'T when 'T : unmanaged>` in `ColPrimitive.fs`.** CLR
generic specialization per value type gives a JIT-inlined, non-virtual
hot path. This is the load-bearing perf decision — don't introduce
virtual dispatch on the bytes-in / bytes-out path. ch-go's ~100
hand-templated `col_*_gen.go` files collapse to one generic class +
sealed leaves here (~70% less code).

**Composites dispatch via facet interfaces** declared in `Columns.fs`:
`IArrayable.Array()`, `INullable.Nullable()`, `ILowCardinality.LowCardinality()`.
These are implemented once on the `ColPrimitive<'T>` base (so all 16
primitives inherit them for free) plus on the typed non-primitive
columns (ColStr, ColEnum8/16, ColJSONStr, ColPoint, ColInterval,
ColNothing). The composite wrappers (`ColArr`, `ColNullable`,
`ColLowCardinality`) also implement `IArrayable` to enable recursion
— `Array(Array(T))`, `Array(Nullable(T))`, `Array(LowCardinality(T))`.

`ColAuto.build` uses these facets for runtime-string → static-generic
dispatch on `Array( / Nullable( / LowCardinality(`. `Tuple(` gets a
depth-aware comma parser. `Map(String, String)` and `Nullable(Nothing)`
are hardcoded fast paths; general `Map(K, V)` resolves via one-time
reflection at construction (`MakeGenericType` + `Activator.CreateInstance`),
with zero hot-path cost.

**Bulk-access facets** (in `Columns.fs`) — `IBulkAppendable<'T>.AppendRange`,
`IBulkReadable<'T>.AsSpan`, `IRowBytes.RowBytes` — implemented on
`ColPrimitive<'T>` (free for all primitives) and on `ColStr` /
`ColFixedBytes`. `ColArr<'T>.AppendSpan` / `RowSpan` and
`ColNullable<'T>.ValueSpan` dispatch through them for zero-alloc bulk
paths when the inner column is bulk-capable.
`ColLowCardinality<'T>.RowSpan` exposes the dict's byte slice when
`inner` is `IRowBytes` (works for `LowCardinality(String)` and
`LowCardinality(FixedString(N))`). `ColLowCardinality<'T>` does inline
dedup at `Append` time and materialises its `'T[]` dict lazily on
first `Row(i)` — byte-span consumers skip it entirely.
`LowCardinality(FixedString(N))` uses
`ByteArrayContentEqualityComparer` (FNV-1a) for content dedup.

### Internal storage for offset / null-mask buffers

`ColArr` / `ColNullable` / `ColMap` store their offsets / null bytes in
**raw arrays** (`uint64[]` / `byte[]` / `uint64[]`), not in typed
`ColUInt64` / `ColUInt8` columns. This breaks what would otherwise be a
`ColPrimitive → ColArr → ColUInt64 → ColPrimitive` circular dependency
and lets the facet interfaces live on the `ColPrimitive` base.
Wire-format-identical to the typed version on LE x64 via
`MemoryMarshal.AsBytes` — do not "tidy up" by re-introducing typed
column storage there.

## Project-specific conventions

These will bite if you don't know them:

- **F# compile order matters.** Every new `.fs` file MUST be added to
  `src/Ch.Proto/Ch.Proto.fsproj` `<Compile Include="...">` in dependency
  order. The current order: `Columns.fs` (interfaces only) →
  `ColArr.fs` → `ColNullable.fs` → `ColLowCardinality.fs` →
  `ColPrimitive.fs` (the 16 primitive leaves) → typed non-primitives →
  `ColAuto.fs`. Don't reorder casually.
- **`Buf`, not `Buffer`.** `System.Buffer` is a BCL static class that
  shadows our type once you `open System` — we renamed ours to `Buf`.
- **Buffer growth: `Array.Resize(&buf, newCap)`, never `Array.zeroCreate`.**
  Resize preserves contents; `zeroCreate` doesn't. (Bit us in commit
  `c9d66d6`.)
- **Generic writes via `MemoryMarshal.Write<'T>`** — `Span<'T>.[0] <- v`
  doesn't always persist for generic `'T` (same commit).
- **Don't sprinkle `not null` on generic params** unless the type
  actually requires it (Dictionary key, etc.). It propagates to callers
  through Nullable=enable strict.
- **Skip state header on empty blocks.** The server omits the
  per-block state header when `rowCount = 0`. `Client.fs` handles
  this; new `IStatefulColumn` impls must not assume state is always
  present.
- **Server's type string ≠ client's `Column.Type`.** `ColumnType.normalize`
  in `Columns.fs` folds `Decimal(P, S)`, parameterized `Enum8(...)`,
  and `DateTime('tz')` / `DateTime64(N, 'tz')` to bare forms. Extend
  that table when adding new parameterized columns.
- **`--insert` smoke has a known driver-side timing race.** Server
  receives the INSERT but parses zero data rows (`query_log:
  written_rows=0`) even though the encoded bytes are correct. The
  `Thread.Sleep(200)` between CREATE and INSERT doesn't actually fix
  it (verified — pre-existing tables also fail). Active hypothesis:
  the external-tables blank sentinel sent right after the Query
  packet gets misclassified as an input-data end-of-data marker
  under tight timing, finalising the INSERT before our real data
  block arrives. See `plans/HANDOVER.md` milestone #1.
  *(Previously misattributed to ClickHouse's `Atomic` database engine
  — that's a server-side metadata-management mode, nothing to do with
  ACID transactions, and not actually our bug. The misnomer caused
  months of misdiagnosis; don't repeat it.)*
- **One column type per file** (`src/Ch.Proto/Col*.fs`). Close cousins
  may share a file (e.g. `ColTime.fs` holds Date / Date32 / DateTime /
  DateTime64 / IPv4; `ColFixed.fs` holds FixedStr / UUID / IPv6).
  Tests follow the same per-file pattern under `tests/Ch.Proto.Tests/`.
- **`TreatWarningsAsErrors=true`** is on globally (Directory.Build.props).
  A warning fails the build — fix the root cause, don't suppress.

## Pre-1.0 API stability

We have no external consumers. **Pre-1.0 breaking changes are
encouraged when they improve the design.** Don't add
backwards-compatibility shims, deprecation wrappers, or migration
helpers — just change the code. (Versioning is in `Directory.Build.props`.)

## Triaging proposed changes

Surface-level API changes (record fields, method names, sync vs async)
are cheap and can move fast. Changes that touch type fidelity or the
encode/decode hot path (column internals, wire format, generic
specialization, virtual dispatch) need careful review — they're where
the perf parity with ch-go lives. When in doubt, A/B against
`bench/go/` before merging.

## Where to look for more context

- `plans/HANDOVER.md` — session-resumption doc: current state, coverage
  table, gotchas, open questions. Read this first when picking up cold.
- `plans/DESIGN_CHOICES.md` — running log of intentional departures
  from ch-go. Append a numbered entry when adding another departure.
- `plans/PERF_INVESTIGATION.md` — full bench methodology + evidence
  for the perf-parity claim. Useful when revisiting hot-path changes.
- `FSHARP.md` — general F# style notes (much of it is generic; the
  Expecto and interpolated-string sections apply here).
- `README.md` — public-facing API tour with code samples for every
  column type. Authoritative reference for the user-visible surface.
