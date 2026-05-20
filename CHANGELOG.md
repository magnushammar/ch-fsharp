# Changelog

## 0.5.1

### Added

- **Test coverage for the Tier 1 / Tier 2 SELECT API.** `RetryTests.fs` — 17 cases, server-free:
  - `RetryPolicy.isRetryableNetwork` classifier across every retryable shape (`IOException` and a subclass, `SocketException`, `TimeoutException`, `ObjectDisposedException`, `UnexpectedEndOfStreamException`) and the non-retryable ones (`InvalidDataException`, plain `Exception`, an F# exception).
  - `RetryPolicy.defaults` field values, and that its classifier routes through `isRetryableNetwork`.
  - `RetryCore.run` control loop — first-try success, retry-then-recover, `MaxAttempts` exhaustion asserting the exact `1-2-4-8 s` backoff schedule, non-retryable fast-fail, the gate-blocks-retry no-double-count property, and `MaxAttempts = 1`.

  `ColIntoArrayTests.fs` gains 3 cases: `ElemSize` across the primitive set, a `Bool` 1-byte-per-row decode through the `MemoryMarshal` reinterpret, and a truncated-stream decode (throws `UnexpectedEndOfStreamException`, `Rows` unchanged). Suite: 248 → 268 tests.

### Changed

- **`Retry.loop` refactored for testability.** The retry control loop is extracted into an `internal RetryCore.run` that takes the attempt thunk and the `sleep` function as parameters, so the classifier ∧ gate ∧ attempt-count guard and the `BaseBackoffMs · 2ⁿ` backoff schedule are unit-testable without a live server. Behaviour is byte-for-byte identical; `Retry.loop` is the sole production caller and supplies the real `Client.Connect` + `Thread.Sleep`. `RetryCore` is `internal` — the public surface of `Ch.Client` is unchanged.
- **`tests/Ch.Proto.Tests` now references `Ch.Client`** (required to test `Ch.Stream.Retry` / `RetryCore`), and `Ch.Client` exposes its internals to the test assembly via `InternalsVisibleTo`. The offline `build` and `test` commands still work without a server.

### Not changed

- No protocol changes, no public API changes; this is a test-coverage + internal-refactor release.

---

## 0.5.0

### Added

- **`Ch.Stream`** (new namespace + static class in `Ch.Client.Stream`) — Tier 1 streaming SELECT helpers. Three layers:
  - `Stream.rows_<shape>(client, sql, action)` — per-row typed dispatch.
  - `Stream.blocks_<shape>(client, sql, onBlock)` — per-block `ReadOnlySpan<'T>` dispatch via custom delegates (`OnBlock1`…`OnBlock8`); F# lambdas auto-convert at the member-call site.
  - `Stream.columns(client, sql, cols, onBlock)` — escape hatch over `Client.Select`, takes `IColumnResult list` directly.

  Shape set covers `Int64`/`UInt64`/`Int32`/`UInt8`/`Float64`/`Nullable(Float64)`/`Array(Float64)` combinations actually exercised today; wide / pathological shapes stay on `Stream.columns`.

- **`Ch.Drain.ColIntoArray<'T>(dst)`** (new column type in `Ch.Proto`, re-exported under `Ch.Drain`) — Tier 2 SELECT-only column that decodes server rows directly into a caller-owned typed array. `Rows` is the live offset; `AsSpan()` returns the decoded prefix and is stable across blocks (the byte buffer never resizes). Throws on overflow; throws on `EncodeColumn`. For SPSC drain patterns where one thread fills the array and another reads it against a `Volatile.Write(&_, col.Rows)` frontier.

- **`Ch.Client.ColumnResult.ofColumn`** — helper that wraps an `IColumnResult` as a positional `ColumnResult` (empty `Name`). Replaces the `{ Name = ""; Column = c :> IColumnResult }` boilerplate at every positional callsite.

- **`Ch.Stream.Retry`** — opt-in bounded retry-with-backoff. `RetryPolicy` record + `RetryPolicy.defaults` (5 attempts, 1-2-4-8 s exponential backoff, `RetryPolicy.isRetryableNetwork` classifier for `IOException` / `SocketException` / `TimeoutException` / `ObjectDisposedException` / `UnexpectedEndOfStreamException`). Static `Retry` class mirrors the `Stream.*` shape set; each helper opens a fresh `Client` per attempt and re-runs the action on classified-transient failures occurring *before* the action's first stateful work (the `gate` is flipped internally on the per-row / per-block boundary, so callers don't think about it). `Retry.run(opts, policy, action)` is the escape hatch for `Stream.columns` users — exposes the `bool ref` gate so the caller can mark stateful-work start manually. Connection pooling is intentionally *not* shipped — `Client.Connect` is ~0.5 ms on localhost (`tmp/bench-connect.fsx`); per-attempt fresh connections sidestep stale-socket and session-state-bleed concerns.

### Changed

- **`ColPrimitive.AsSpan()` xmldoc** strengthened: the returned span is exactly `Rows` items, not the underlying buffer capacity. No further `.Slice(0, rows)` is required. Behavior unchanged — documentation only.

### Not changed

- No protocol changes; the 236 pre-existing `Ch.Proto.Tests` all pass.
- `Client.Select` / `Client.Insert` / `SelectQuery` / `InsertQuery` shapes are unchanged. `Ch.Stream` and `Ch.Drain` are pure additions layered on top.

### Deferred

- `ColAccumulating<'T>` (grow-on-decode variant of `ColIntoArray`). Pre-sized strictly subsumes accumulation when the total row count is cheap to obtain via `count()`. Add when a consumer needs unsized drain.
- `Ch.Drain.arrays_<shape>` (high-level tuple-of-arrays helpers). Add when the manual `ColIntoArray ×N` idiom appears in more than one feature.

---

## Migration recipe (consumer-side)

This driver has one consumer (`games-with-numbers`); the recipe targets that codebase. The downstream `Runner.ChfsConn` module presently wraps `Client.Select` with retry + connection pooling and exposes a per-row `streamI64F64…` family plus a generic `streamColumns` escape hatch.

Mapping legacy `ChfsConn` shapes to the new tiered API:

| Legacy `Runner.ChfsConn`                                                    | New (with retry)                                              | New (no retry)                                                |
|-----------------------------------------------------------------------------|---------------------------------------------------------------|----------------------------------------------------------------|
| `streamI64F64(sql, action)`                                                 | `Retry.rows_i64f64(opts, policy, sql, action)`                | `Stream.rows_i64f64(client, sql, action)`                      |
| `streamI64F64F64(sql, action)` … `streamI64F64F64F64F64F64F64F64(…)`        | `Retry.rows_i64f64f64(…)` … `Retry.rows_i64f64x7(…)`           | `Stream.rows_i64f64f64(…)` … `Stream.rows_i64f64x7(…)`         |
| `streamI64I32`, `streamI64F64I32`, `streamI64F64F64I32`                     | `Retry.rows_i64i32`, `Retry.rows_i64f64i32`, …                | `Stream.rows_i64i32`, `Stream.rows_i64f64i32`, …               |
| `streamI64F64N`                                                             | `Retry.rows_i64f64n`                                          | `Stream.rows_i64f64n`                                          |
| `streamI64A64A64`, `streamI64I64A64A64`, `streamI64I64A64A64A64A64`         | `Retry.rows_i64a64a64`, `Retry.rows_i64i64a64a64`, …          | `Stream.rows_i64a64a64`, `Stream.rows_i64i64a64a64`, …         |
| New: trade-stream shape (`Int64+Int64+Float64+Float64+UInt8`)               | `Retry.rows_i64i64f64f64u8` / `Retry.blocks_i64i64f64f64u8`   | `Stream.rows_i64i64f64f64u8` / `Stream.blocks_i64i64f64f64u8` |
| `streamColumns(sql, [ ChfsConn.col c0; … ], onBlock)`                       | `Retry.run(opts, policy, fun client gate -> Stream.columns(client, sql, cols, fun rows -> gate.Value <- true; onBlock rows))` | `Stream.columns(client, sql, [ c0; c1; … ], onBlock)`          |
| `streamRows(sql, cols, action)`                                             | `Retry.run(...) ` (gate manually) wrapping `Stream.columns`   | `Stream.columns(client, sql, cols, fun rows -> for i in 0..rows-1 do action i)` |
| Drain hand-roll: `count()` + `Array.zeroCreate` + `AsSpan().CopyTo` per block | `Retry.run(...)` wrapping `Stream.columns` + `ColIntoArray ×N` | `ColIntoArray(arr) × N` + `Stream.columns`                     |
| `{ Name = ""; Column = c :> IColumnResult }`                                | `ColumnResult.ofColumn c`                                     | `ColumnResult.ofColumn c`                                      |
| `ChfsConn.col c`                                                            | `ColumnResult.ofColumn c` (lifted into driver)                | `ColumnResult.ofColumn c`                                      |

Pick the **with retry** column when porting from `runSelectWithRetry`; pick **no retry** when the consumer owns the retry envelope or doesn't want one (e.g. fast-fail probes).

### Connection pool: drop it; use `Retry.*` instead

The current `Runner.ChfsConn` carries a `ThreadLocal<Client>` pool plus `runSelectWithRetry` (force-dispose-via-CTS, `resetConnection`, `CellTimeoutException` classification, `receivedAny` gate). `Client.Connect` measured at ~0.5 ms on localhost (`ch-fsharp/tmp/bench-connect.fsx`), which means the pool buys ~15 s amortised across a 30 k-query sweep — invisible against typical eval workloads (whole-day scans, multi-second per cell).

What you get by dropping the pool:
- `threadConn`, `connection ()`, `resetConnection ()` — gone.
- Force-dispose-on-CTS via `cts.Token.Register` — gone (`Client.Connect` accepts a CT; per-attempt fresh connections sidestep the synchronous-`Socket.Receive`-doesn't-honor-CT problem).
- Stale-socket-after-server-restart handling — gone (every attempt opens a fresh socket).
- Cross-query session-state bleed — gone (clean session per attempt).

The retry policy moves into the driver: `Ch.Stream.Retry.*` mirrors the `Stream.*` shape set and handles the gate flip internally. Consumer-side wiring per shape becomes one line:

```fsharp
module Runner.ChfsConn.Stream

let private opts = clickhouseOptions ()           // existing per-process opts
let private policy = RetryPolicy.defaults          // or customise

let rows_i64f64 sql action =
    Retry.rows_i64f64(opts, policy, sql, action)

let blocks_i64f64 sql onBlock =
    Retry.blocks_i64f64(opts, policy, sql, onBlock)

let columns sql cols onBlock =
    Retry.run(opts, policy, fun client gate ->
        Stream.columns(client, sql, cols, fun rows ->
            gate.Value <- true        // mark stateful-work start
            onBlock rows))
```

Customise the policy where needed:

```fsharp
let private cellPolicy = {
    RetryPolicy.defaults with
        MaxAttempts = 3
        BaseBackoffMs = 500
        IsRetryable = fun ex ->
            RetryPolicy.isRetryableNetwork ex
            || (ex :? Logging.CellTimeoutException)
}
```

(Or eliminate `CellTimeoutException` entirely — without the pool's force-dispose-via-CTS hack, timeouts surface as plain `IOException` / `SocketException` and the default classifier already retries them.)

### Drain — `BookTradeImpactDecay.fs` book producer

**Before** (hand-rolled drain — current code at lines 308-376):

```fsharp
let bookCount = int (ChfsConn.scalarU64 bookCountSql)
let bookEpoch = Array.zeroCreate<int64> bookCount
let bookBp1   = Array.zeroCreate<float> bookCount
let bookAp1   = Array.zeroCreate<float> bookCount
let bookBq1   = Array.zeroCreate<float> bookCount
let bookAq1   = Array.zeroCreate<float> bookCount

let cBE  = ColInt64()
let cBp1 = ColFloat64()
let cAp1 = ColFloat64()
let cBq1 = ColFloat64()
let cAq1 = ColFloat64()
let mutable bookN = 0

ChfsConn.streamColumns bookSql
    [ ChfsConn.col cBE;  ChfsConn.col cBp1; ChfsConn.col cAp1
      ChfsConn.col cBq1; ChfsConn.col cAq1 ]
    (fun rows ->
        cBE.AsSpan().Slice(0, rows).CopyTo(Span<int64>(bookEpoch, bookN, rows))
        cBp1.AsSpan().Slice(0, rows).CopyTo(Span<float>(bookBp1, bookN, rows))
        cAp1.AsSpan().Slice(0, rows).CopyTo(Span<float>(bookAp1, bookN, rows))
        cBq1.AsSpan().Slice(0, rows).CopyTo(Span<float>(bookBq1, bookN, rows))
        cAq1.AsSpan().Slice(0, rows).CopyTo(Span<float>(bookAq1, bookN, rows))
        bookN <- bookN + rows
        Volatile.Write(&bookPub.[0], bookN))
```

**After** (`ColIntoArray` + `Stream.columns`):

```fsharp
let bookCount = int (ChfsConn.scalarU64 bookCountSql)
let bookEpoch = Array.zeroCreate<int64> bookCount
let bookBp1   = Array.zeroCreate<float> bookCount
let bookAp1   = Array.zeroCreate<float> bookCount
let bookBq1   = Array.zeroCreate<float> bookCount
let bookAq1   = Array.zeroCreate<float> bookCount

let cBE  = ColIntoArray<int64>(bookEpoch)
let cBp1 = ColIntoArray<float>(bookBp1)
let cAp1 = ColIntoArray<float>(bookAp1)
let cBq1 = ColIntoArray<float>(bookBq1)
let cAq1 = ColIntoArray<float>(bookAq1)

ChfsConn.Stream.columns bookSql
    [ cBE :> IColumnResult; cBp1 :> IColumnResult; cAp1 :> IColumnResult
      cBq1 :> IColumnResult; cAq1 :> IColumnResult ]
    (fun _rows -> Volatile.Write(&bookPub.[0], cBE.Rows))
```

What goes away: five `Col*()` declarations, five `ChfsConn.col` wraps, five per-block `MemoryMarshal.Cast` + `CopyTo` calls, the `bookN` offset bookkeeping, the `.Slice(0, rows)` cargo-cult. The consumer side (cursor walks against `bookEpoch.[iPre]`, `bookBp1.[i500]`, …) is unchanged — those arrays are the same caller-owned objects passed to `ColIntoArray`.

### Streaming — `BookTradeImpactDecay.fs` trade consumer

**Before** (lines 381-436):

```fsharp
let cEpoch = ColInt64()
let cBar   = ColInt64()
let cQty   = ColFloat64()
let cPrice = ColFloat64()
let cIbm   = ColUInt8()
ChfsConn.streamColumns tradesSql
    [ ChfsConn.col cEpoch; ChfsConn.col cBar; ChfsConn.col cQty
      ChfsConn.col cPrice; ChfsConn.col cIbm ]
    (fun rows ->
        let epochs = cEpoch.AsSpan()
        let bars   = cBar.AsSpan()
        let qtys   = cQty.AsSpan()
        let prices = cPrice.AsSpan()
        let ibms   = cIbm.AsSpan()
        for i in 0 .. rows - 1 do
            …)
```

**After** (`Stream.blocks_i64i64f64f64u8`):

```fsharp
ChfsConn.Stream.blocks_i64i64f64f64u8 tradesSql (fun epochs bars qtys prices ibms ->
    for i in 0 .. epochs.Length - 1 do
        …)
```

Five `Col*()` declarations, five `ChfsConn.col` wraps, five `.AsSpan()` calls all gone.

### Verification

After the refactor, run one ALICEUSDT day through the BookTradeImpactDecay feature and diff the produced cache cells against a pre-refactor baseline (the timing line at `BookTradeImpactDecay.fs:442` already exists). Byte-identical output is the bar; the diff target is the code-volume reduction, not wall time.
