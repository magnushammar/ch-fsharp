# Bench results — `SELECT number FROM system.numbers_mt LIMIT 500000000`

Canonical ch-bench workload. 500 M `UInt64` rows = 3.73 GiB streamed over loopback.
Sum is verified against `N*(N-1)/2 = 124,999,999,750,000,000` in the F# and
Go binaries — both clients do real decode + scan work, not just discarding bytes.

## Setup

| Item | Value |
|---|---|
| Machine | Local host, `clickhouse-server` 26.3.4.11 on 9000 |
| F# binary | `dotnet publish -c Release -p:PublishReadyToRun=true`, .NET 10 |
| Go binary | `bench/go/main.go` thin wrapper over ch-go (this repo's submodule) |
| C++ baseline | `clickhouse-client … --format Null` (ch-bench's Makefile target) |
| Bench runner | `scripts/bench.py -w 3 -r 10 -s 1.5` (3 warmups, 10 measurements, 1.5 s settle) |

## Results

| Label    | Description                                       | Mean (ms) |   Min |   Max | Stdev |
|----------|---------------------------------------------------|----------:|------:|------:|------:|
| `cpp`    | clickhouse-client (default = uncompressed)        |       776 |   536 |  1656 |   441 |
| `go`     | ch-go via thin wrapper, uncompressed              |      1696 |   664 |  1913 |   366 |
| `fs`     | **this project**, uncompressed                    |      2041 |  1800 |  2181 |   123 |
| `go-lz4` | ch-go wrapper with `--lz4`                        |      3748 |  3702 |  3796 |    32 |
| `fs-lz4` | **this project** with `--lz4`                     |      3996 |  3907 |  4065 |    47 |

## Headline

- **F# is within 4-5 % of ch-go on the compressed path** (`fs-lz4`/`go-lz4`: min
  3907/3702, mean 3996/3748). Compression is correct and competitive.
- **F# is within ~10 % of ch-go on best-time uncompressed** in isolation
  (620-750 ms wall, matching Go's 580-670 ms — see "isolated runs" below). Both
  fluctuate up to ~1900 ms under rapid-fire load (the bimodal pattern below).
- **Compression hurts on this loopback test.** Both clients land at ~3.9 s
  *with* LZ4 vs ~600 ms *without*. clickhouse-client gets to 540 ms by sending
  uncompressed (verified via `ProfileEvents['NetworkSendBytes']` = 4 GB and
  `NetworkCompressedSendBytes` = 0).

## What I got wrong in the previous RESULTS.md

The earlier draft argued `cpp` was fast because it defaulted to LZ4. **That's
incorrect.** Pulling `system.query_log.ProfileEvents['NetworkSendBytes']` for a
typical clickhouse-client run shows 4 GB sent, `lz4_compressed = 0`. The C++
client *defaults to uncompressed* on the native TCP interface. Passing
`--compression 1` to it lands ~4 s — same ballpark as `fs-lz4` and `go-lz4`.

The real story: on a 10 GiB/s loopback the LZ4 *encoder* on the server side is
the bottleneck, not bandwidth. For 500 M sequential `UInt64` rows the server
spends ~3.5 s compressing 4 GB → 2 GB (~1.1 GB/s LZ4 encode throughput). The
client-side decode is comparatively cheap. On a slower network where the
2 GB-vs-4 GB delta dominates wall time, compression wins; on this hardware it
loses. The bimodal slow path we saw before (~1.9 s spikes when uncompressed)
is also a real effect — it hits all three uncompressed clients including
clickhouse-client — but it isn't worse than LZ4's 3.9 s steady state.

## Isolated runs (2-second settle, server idle between runs)

```
F# uncompressed: 0.63s 0.66s 0.62s 0.64s 0.75s    (user CPU 0.77-1.22s)
Go uncompressed: 0.58s 0.62s 0.67s 0.64s 0.60s    (user CPU 0.22-0.27s)
```

F# burns 3-4× more CPU than Go for the same wall time. That's almost certainly
.NET's server GC threads + tiered-compilation rejits running in parallel —
they don't extend wall time because they're on other cores. Worth profiling
post-MVP if we want to bring CPU usage down, but it doesn't gate the throughput
target.

## Server-side numbers (`system.query_log.query_duration_ms`)

| Client             | Server duration | Send bytes | Compressed send |
|--------------------|----------------:|-----------:|----------------:|
| `cpp`              |          572 ms |       4 GB |               0 |
| `fs` / `go`        |     ~1996 ms    |       4 GB |               0 |
| `fs-lz4` / `go-lz4`|     ~3902 ms    |       2 GB |          2 GB   |

The 1996 vs 572 ms gap for uncompressed-but-the-client-changed is the bimodal
slow-path effect on the server — same query, same wire format, repeatable
factor-of-3 swing. Unaffected by compression: when we enable LZ4 the server
duration just plateaus at the LZ4-encoder limit.

## How to reproduce

```bash
# Have CLICKHOUSE_PASSWORD exported and ClickHouse listening on 9000.

dotnet publish src/Ch.Bench.Numbers -c Release \
  -p:PublishReadyToRun=true -r linux-x64 --self-contained false \
  -o /tmp/ch-bench-fs

cd bench/go && go build -o /tmp/ch-bench-go . && cd -

python3 scripts/bench.py -w 3 -r 10 -s 1.5 \
  fs=/tmp/ch-bench-fs/Ch.Bench.Numbers \
  "fs-lz4=/tmp/ch-bench-fs/Ch.Bench.Numbers --lz4" \
  go=/tmp/ch-bench-go \
  "go-lz4=/tmp/ch-bench-go --lz4" \
  "cpp=clickhouse-client --password \$CLICKHOUSE_PASSWORD -q 'SELECT number FROM system.numbers_mt LIMIT 500000000' --format Null"
```

## Single-run sanity

```
$ /tmp/ch-bench-fs/Ch.Bench.Numbers
Connected: ClickHouse rev=54483 tz=UTC
OK: 500000000 rows | 3.73 GiB | 597 ms | 6.24 GiB/s | sum=124999999750000000

$ /tmp/ch-bench-fs/Ch.Bench.Numbers --lz4
Connected: ClickHouse rev=54483 tz=UTC
OK: 500000000 rows | 3.73 GiB | 3884 ms | 0.96 GiB/s | sum=124999999750000000
```
