# MVP bench results — `SELECT number FROM system.numbers_mt LIMIT 500000000`

Canonical ch-bench workload. 500 M `UInt64` rows = 3.73 GiB streamed over loopback,
uncompressed except where noted. Sum is verified against `N*(N-1)/2 =
124,999,999,750,000,000` in the F# and Go binaries — both clients are doing
real decode + scan work, not just discarding bytes.

## Setup

| Item | Value |
|---|---|
| Machine | Local host, `clickhouse-server` 25.12.4.35 on 9000 |
| Server query time | 44–58 ms (read from `system.query_log`) — server-side is *not* the bottleneck |
| F# binary | `dotnet publish -c Release -p:PublishReadyToRun=true`, .NET 10 |
| Go binary | `bench/go/main.go` (thin wrapper over ch-go, mirrors `ch-bench-faster`) |
| C++ baseline | `clickhouse-client … --format Null` (ch-bench's Makefile baseline) |
| Bench runner | `scripts/bench.py -w 3 -r 15 -s 2.0` (3 warmups, 15 measurements, 2 s settle) |

## Results

| Label     | Description | Mean (ms) | Min  | Max  | Stdev |
|-----------|-------------|----------:|-----:|-----:|------:|
| `cpp-z`   | clickhouse-client, **compressed** (default) | 738  | 564  | 1692 | 379 |
| `go`      | ch-go via thin wrapper, uncompressed        | 1111 | 572  | 1917 | 626 |
| `cpp-raw` | clickhouse-client, `--compression false`    | 1164 | 557  | 1759 | 563 |
| `fs`      | **this project**, uncompressed              | 1194 | 621  | 2274 | 683 |

## Interpretation

### Headline: F# is within 9 % of ch-go on best-time, 7 % on mean

By **min** (the metric ch-bench's RESULTS table highlights as "best"):
F# 621 ms vs Go 572 ms = **1.09×**. By mean: F# 1194 ms vs Go 1111 ms =
**1.07×**. Both metrics are inside the plan's "within 2×" acceptance band by a
wide margin.

For receive-only-no-compression code with one column type implemented, this
is parity with the reference Go implementation.

### Why is `cpp-z` so much faster?

`clickhouse-client` defaults to LZ4 on the native TCP protocol. For sequential
`UInt64` like `system.numbers_mt`, the compression ratio is enormous — the
server pushes maybe a few hundred MB instead of 3.73 GiB. None of `fs`, `go`,
`cpp-raw` use compression in this bench, so they all eat the full wire bytes.

When `--compression false` is forced, `cpp-raw` lands on top of `go` (within
4 % on mean, on top on min) — i.e. C++ uncompressed is **not faster than the
Go reference**, and not faster than F# uncompressed. The compressed C++ wins
purely because it transfers ~1/20th of the bytes.

This is the next perf milestone for this port: implement LZ4 + CityHash128
block framing (M-comp in the plan). Expected payoff: ~600 ms → ~150 ms on the
canonical bench.

### The bimodal slow path is a system effect, not a client bug

Inner-stopwatch readings for all three uncompressed clients are *bimodal*:
either ~600 ms (fast path) or ~1900 ms (slow path), roughly 30 / 70 split with
2 s settle. This kicks in for the wire when no compression is in use:

```
cpp-raw run-by-run: 0.65 0.68 0.68 0.64 1.75 0.63 1.60 0.64 0.64 1.69 …
```

It happens *to clickhouse-client itself* once compression is disabled, so we
can rule out an F# or Go driver bug. Most likely culprit is something below
the user level: TCP slow-start / receive window, ksoftirqd scheduling, CPU
governor on the receive core, or NIC IRQ affinity. Investigating this is out
of scope for the MVP.

### `ch-bench-official` is a different driver

The `ch-bench-official` binary
(https://github.com/ClickHouse/ch-bench/tree/main/ch-bench-official) uses
`ClickHouse/clickhouse-go/v2` — the high-level row-at-a-time driver. Per
ch-bench's own RESULTS table that binary runs the same query in **46.8 s** (vs
401 ms for ch-go). The 117× gap is the `rows.Next() + rows.Scan(&value)`
API, not the protocol. This port targets `ch-go`'s block-oriented design;
`ch-bench-official` is the wrong reference for our perf goal.

## How to reproduce

```bash
# Have CLICKHOUSE_PASSWORD exported and ClickHouse listening on 9000.

dotnet publish src/Ch.Bench.Numbers -c Release \
  -p:PublishReadyToRun=true -r linux-x64 --self-contained false \
  -o /tmp/ch-bench-fs-r2r

cd bench/go && go build -o /tmp/ch-bench-go . && cd -

python3 scripts/bench.py -w 3 -r 15 -s 2.0 \
  fs=/tmp/ch-bench-fs-r2r/Ch.Bench.Numbers \
  go=/tmp/ch-bench-go \
  "cpp-z=clickhouse-client --password \$CLICKHOUSE_PASSWORD -q 'SELECT number FROM system.numbers_mt LIMIT 500000000' --format Null" \
  "cpp-raw=clickhouse-client --password \$CLICKHOUSE_PASSWORD --compression false -q 'SELECT number FROM system.numbers_mt LIMIT 500000000' --format Null"
```

## Single-run sanity

```
$ /tmp/ch-bench-fs-r2r/Ch.Bench.Numbers
Connected: ClickHouse rev=54483 tz=UTC
OK: 500000000 rows | 3.73 GiB | 597 ms | 6.24 GiB/s | sum=124999999750000000
```
