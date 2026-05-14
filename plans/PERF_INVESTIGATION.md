# Performance Investigation — F# driver vs ch-go

Status: **concluded — the F# driver is ~25–35 ms *faster* than ch-go's;
the headline wall time is ~2 % behind on a server-paced workload, and
that gap is the bench's `sum` scaffolding, not the driver.**
This is the full investigation log: every hypothesis tried, the
evidence, and what actually moved the needle. Parts 1–6 are the log in
the order things happened; **Part 7** was an interim resolution and
**Part 8 supersedes its "parity" claim** — a fair alternating-protocol
measurement plus a driver/sum diagnosis. Earlier parts' committed
changes are in "Repo state"; Part 8's work is in the working tree.

## The workload

ch-bench-official's canonical query:

```sql
SELECT number FROM system.numbers_mt LIMIT 500000000
```

3.73 GiB of UInt64 streamed, summed client-side so the decode can't be
elided. Timed the way ch-bench-official times it: a `Stopwatch` /
`time.Now()` wrapping just the query + row iteration — **not** process
startup, **not** TCP connect/handshake. Our `bench/fs/Program.fs` and
`bench/go/main.go` both measure this same boundary.

Hardware: i7-13700KF — 8 P-cores (CPUs 0–15, HT, ~5.3–5.4 GHz) + 8
E-cores (CPUs 16–23, ~4.2 GHz, much weaker IPC). Server and client share
the machine. Governor `powersave`. PL1 = PL2 = 253 W (no turbo-budget
cliff), package idles ~27–39 °C.

## Part 1 — the 3× variance was the scheduler, not the driver

First measurements swung wildly: the same binary, same query, ran
~600 ms sometimes and ~1800–2100 ms other times. Ruled out, with
evidence:

- **Not process startup.** The Stopwatch is inside the program, around
  `Do` only. A standalone run was *also* slow (2141 ms) — startup is
  constant and excluded anyway.
- **Not power/thermal throttling.** RAPL shows PL1 = PL2 = 253 W — the
  short-term and long-term limits are *equal*, so there is no turbo
  budget that drains. Package temps were 27–39 °C. Cold, unthrottled.
- **It is P-core vs E-core scheduling.** Our receive/decode loop is
  single-threaded. `taskset` proved it:

  | pinning | runs (ms) | GiB/s |
  |---|---|---|
  | P-cores `taskset -c 0-15` | 569 516 554 523 542 | ~6.9 |
  | E-cores `taskset -c 16-23` | 1029 1061 1075 1030 1084 | ~3.5 |

  Clean ~2× P-vs-E, and **tight once pinned**. Unpinned, the single
  decode thread lands wherever the scheduler puts it; when the server's
  ~24 generator threads are busy they own the P-cores and our thread
  gets an E-core or a contended HT sibling. A lone run on an idle
  machine catches a free P-core (~540 ms); a run while the server is
  warm gets crowded out (~1800 ms). Not "first vs not-first" —
  **idle-machine vs server-busy.**

**Consequence for benchmarking:** every comparison from here on pins the
client with `taskset -c 0-15`. *Even so*, the server still floats onto
those same cores — see "Open problems".

## Part 2 — the honest baseline (both pinned to P-cores 0–15)

10 runs each, 3 s pause, sequential (not interleaved):

| | min | median | mean | max | stdev |
|---|---|---|---|---|---|
| **fs** | 519 | 561 | 562 | 625 | 38 |
| **go** | 458 | 490 | 491 | 527 | 20 |

**F# ≈ 1.13× min, 1.14× median/mean.** That ~14% / ~70 ms is the real
gap to explain. (The earlier "1.23–1.25×" from `scripts/bench.py` was
inflated — that harness times `subprocess.run`, i.e. total process
wall, so it folded ~200 ms of .NET startup into every F# sample.)

## Part 3 — syscall census

`strace -f -c` on a single pinned run:

| syscall | F# | Go |
|---|---|---|
| total | **295,974** | **31,357** |
| `sched_yield` | **224,940** | **0** |
| `rt_sigprocmask` | 30,930 | 37 |
| `recvmsg` / `read` (socket) | 16,048 | 15,915 |
| `epoll_wait` / `epoll_pwait` | 16,234 | 13,480 |
| `futex` | 949 | 236 |

The I/O syscall *counts* are comparable. The glaring F#-only cost is
**224,940 `sched_yield`** — ~14 per socket read. That is spin-waiting.

## Part 4 — things tried

### 4a. `SO_RCVBUF` 16 MB + `Socket.Blocking = true` — no effect
Hypothesis: a bigger kernel receive buffer means `recvmsg` always finds
data ready, never `EAGAIN`, never spins. Result: `sched_yield`
224,940 → 202,236. Marginal. .NET routes socket I/O through its
epoll-based `SocketAsyncEngine` regardless of the `Blocking` flag, and
the synchronous-wait path spins before parking either way.

### 4b. Workstation GC (`DOTNET_gcServer=0`) — no effect
`Directory.Build.props` had `ServerGarbageCollection=true`, which
spawns one GC heap/thread per core (24 here) — wrong for a
single-threaded client. But switching to Workstation GC left
`sched_yield` at ~226 K. Not the GC threads.

### 4c. `BlockingFdStream` — true blocking `read(2)` via P/Invoke
New `src/Ch.Proto/BlockingFdStream.fs`: a `Stream` over the raw fd that
does blocking `read(2)`/`write(2)` syscalls directly, with the fd's
`O_NONBLOCK` cleared via `fcntl`. The idea: a true blocking `read(2)`
parks the thread in-kernel until data arrives — exactly what Go's
netpoller does for a parked goroutine — with zero spin.

Result: `recvmsg` is **gone**, replaced by `read` (~17 K). `epoll_wait`
dropped 16 K → 9 K. But **`sched_yield` stayed at ~207 K.** So the
socket reads were never the source of the spin. Wall-time unchanged.

(The change is sound and worth keeping — it removes a layer and matches
Go's I/O model — it just wasn't the bottleneck.)

### 4d. Synchronous `DoAsync` — no effect on the spin
Hypothesis: `DoAsync`'s two initial `do!` async writes resume the
receive loop on a **threadpool thread**; when that thread blocks in
`read(2)`, the threadpool's thread-injection logic spawns more workers,
and the idle extras spin on the work-dispatch semaphore. Fix tried:
added a synchronous `Buf.WriteToAndReset` and made `DoAsync` flush
synchronously, so the whole `task` runs inline on the caller's thread.
Result: `sched_yield` still ~199 K. Either the task still hops, or the
spinning threads come from elsewhere.

### 4e. `DOTNET_ThreadPool_UnfairSemaphoreSpinLimit=0` — kills the spin
This **worked**: `sched_yield` 207,694 → **4,000** (98 % gone), total
syscalls 266 K → 89 K. So the spin *is* the threadpool's
`UnfairSemaphore` — threadpool workers spinning while looking for work.
**But wall-time barely moved** (603 ms, inside the 519–625 ms band).
The spinning workers run on *other* cores; killing the spin cleans up
the syscall trace but doesn't obviously recover wall-time. (Caveat:
pinned to 0–15, the spinners *do* share those cores — a cleaner A/B is
still needed.)

### 4f. `BufferedStream` buffer-size sweep — inconclusive (noise)
Hypothesis: `BufferedStream` is 128 KB but blocks are 512 KB. Every
block, `ReadFull` drains ≤128 KB out of the buffer (a memcpy) then
reads the rest direct — ~128 KB of double-copy per block, ~1 GB of
extra memcpy over the run. Sweep of 4 K / 16 K / 64 K / 128 K / 512 K:
the machine drifted back into slow-mode mid-sweep (2000 ms runs), so
the result is dominated by P/E-core noise. Suggestive only: the *only*
fast runs that appeared were at 4 K and 16 K buffers. **Needs a re-run
with the server pinned off the client's cores.**

## Part 5 — the memory-bandwidth ceiling

Single-thread bandwidth, measured with a C microbench (`gcc -O3
-march=native`, 2 GiB arrays, well above the 30 MB L3):

| | read | copy (r+w) |
|---|---|---|
| **P-core** | **24.1 GB/s** | 45.6 GB/s |
| E-core | 10.0 GB/s | 16.0 GB/s |

(The E-core being 2.4× slower on read independently confirms Part 1.)

What we *achieve* on the wire:

| | wire bandwidth |
|---|---|
| Go | 8.7 GB/s (3.73 GiB / 0.458 s) |
| F# | 7.7 GB/s (3.73 GiB / 0.520 s) |

But wire ≠ memory traffic. Counting client-side copies per payload byte:

| step | traffic |
|---|---|
| `read(2)`: kernel socket buffer → our buffer | 3.73 GiB |
| `BufferedStream` double-copy (≤128 KB/block drained then re-copied) | ~0.9 GiB |
| `OnBlock` sum reads the column buffer | 3.73 GiB |
| **client-side total** | **~8.4 GiB in 0.52 s ≈ 17 GB/s** |

So the F# client decode pushes **~17 GB/s against a 24 GB/s single-P-core
ceiling** — close enough that memory traffic is a real constraint, and
the `BufferedStream` double-copy is **~11 % of that traffic**. That is
almost exactly the F#-vs-Go gap. The implication: closing the gap is
about **touching memory fewer times**, not faster instructions.

## Part 6 — sum-loop microbench, and the verdict on 5.1/5.2/5.3

A separate doc proposed three F#-specific optimisations. Tested the
load-bearing one (5.3, bounds-check elision) directly with a local
microbench — 256 M `uint64`, summed four ways, pinned to one P-core:

| loop | time | bandwidth | vs idiomatic |
|---|---|---|---|
| idiomatic `for i in 0..span.Length-1 do s <- s + span.[i]` | 114 ms | 18.8 GB/s | — |
| **`Unsafe.Add(&ref, i)`** | 128 ms | 16.7 GB/s | **−11 %** |
| `Vector<uint64>` (AVX2, Count=4) | 108 ms | 19.9 GB/s | +6 % |
| *C ceiling* | — | *24.1 GB/s* | *+28 %* |

**5.3 is refuted for sequential loops on .NET 10.** RyuJIT already
elides the bounds check and autovectorises the idiomatic loop — that's
why it hits 18.8 GB/s. Manual `Unsafe.Add` emits a *scalar* loop the JIT
won't re-vectorise, so it is 11 % *slower*. The proposal's premise
("the JIT often fails to prove safety") describes an older JIT. And it
is doubly moot for the numbers bench: `ColUInt64` decode is
`ReadFull` + `MemoryMarshal.Cast` — there is **no per-element decode
loop at all**. 5.3 *might* still help genuinely data-dependent indexing
(LowCardinality key lookups, varint parsing) — untested, lower priority.

`Vector<uint64>` is the only thing that beats idiomatic (+6 %); the
remaining 17 % vs C is single-accumulator dependency-chain latency (C's
gcc unrolls with multiple accumulators). But the sum is *bench
scaffolding* — both F# and Go do it — so it only matters that the two
are comparable, not that it's maximal.

**5.1 (no `task {}` / closures in the hot path)** — not refuted, still
worth doing. The threadpool-spin is real and the receive loop should be
a plain synchronous function, not wrapped in a computation expression.

**5.2 (fewer copies, `ref struct` cursor)** — confirmed as the #1
lever by Part 5's bandwidth math. The `ref struct` cursor is not
elegance, it's the mechanism for a single-copy read path.

## Part 7 — resolution

Two changes closed the gap. Both are committed.

### 7a. The `sched_yield` storm — synchronous connect (`7df7360`)

Parts 4c–4e circled the spin without naming it. The cause:
`tcp.ConnectAsync` **registers the connection fd with .NET's
`SocketAsyncEngine`**. Because ClickHouse streams a SELECT result
continuously, the fd stays *permanently* readable, so the engine's
epoll thread busy-loops — `epoll_wait` returns instantly, finds no
pending .NET async op (we read via raw `read(2)` through
`BlockingFdStream`), re-arms, repeats — and that loop keeps waking
threadpool workers that then spin on the `UnfairSemaphore` (which is
why 4e's env-var also masked it).

Fix: **connect and handshake synchronously** so the engine is never
engaged. The whole connect → handshake → receive loop now runs inline
on the caller's thread; nothing touches the threadpool or the epoll
engine. Measured on the canonical bench, pinned:

| | before | after |
|---|---|---|
| total syscalls / run | 245,000 | **37,000** (≈ ch-go's 31,000) |
| `sched_yield` / run | 189,000 | **10** |
| `epoll_wait` / run | ~7,500 | **0** |

This also retired the open question from 4d (whether `DoAsync` hopped
to a threadpool thread): a probe confirmed the receive loop runs on
`tid=1` — it was never the loop's thread, it was the engine.

### 7b. The "free L2 win" was a false premise

A follow-up doc proposed a −20 to −50 ms win from avoiding a
ch-go-style extra `Data []uint64` copy and summing L2-hot data. But
**both codebases already decode zero-copy**: our `ColPrimitive`
`DecodeColumn` does a single `ReadFull` straight into a reused `buf`,
`AsSpan()` is a `MemoryMarshal.Cast` view, and ch-go's
`col_uint64_unsafe_gen.go` does the equivalent slice-header trick.
There is no extra copy for either side to remove. Crossed off.

(This also walks back Part 5's framing: the `BufferedStream`
double-copy is real but, once measured properly, ~2–5 ms — not the
~11 %-of-traffic the bandwidth arithmetic suggested. The 8 KB buffer
from `7df7360` already bounds it.)

### 7c. The driver/sum split — measure, don't guess

Instrumented `bench/fs` to time the user-side `OnBlock` sum separately
(`f00fe46`). On a clean pinned run the ~545 ms timed region splits:

| | time | rate |
|---|---|---|
| sum (`OnBlock`, bench scaffolding) | ~158 ms | 25 GB/s — **L2-hot already** |
| driver (header parse + socket read) | ~385 ms | — |

The sum is already at L2 speed (faster than the 18.8 GB/s a DRAM-bound
microbench gives) because `read(2)` writes `buf`, `OnBlock` sums `buf`
immediately, and a 512 KB block fits the 2 MB L2. Nothing to win
there. The driver's ~385 ms is mostly *unavoidable* — blocking in
`read(2)` for the server to produce data, plus the kernel→userspace
copy that Go pays too.

### 7d. Per-block redundant work — fast-path decode (`c48b6c5`)

The addressable slice of the driver time was **algorithmic, not
microarchitectural**. The native protocol sends an identical column
header on every Data block, but the receive loop re-validated it each
block: `Block.decode` materialised the name/type strings
(`reader.Str()` → two allocations per column per block), the handler
ran `ColumnType.isCompatible` (four regex `.Replace` per type string),
and the loop rebuilt the target array + handler closure per block. For
~7,630 blocks: ~15 k string allocs + ~60 k regex ops + ~15 k
array/closure allocs per run — all O(blocks) for an O(1) invariant.

Fix: validate the schema once on the first Data block; every block
after takes a fast path — `Block.decodeFast` skips name/type string
materialisation (`Reader.SkipStr`), and the receive loop hoists the
target arrays + handler closures out and latches `schemaValidated`.

### 7e. Result — steady-state parity

40-run comparison, both clients pinned to P-cores, 3 s pause,
sequential. F# ran first and absorbed 8 bimodal warm-up slow runs (a
measurement-ordering artifact — Part 1's P/E contention; run Go first
and it eats them instead). The **fast cluster** — settled-state runs,
the fair comparison — is:

| | n | min | p5 | median | mean | stdev |
|---|---|---|---|---|---|---|
| **F#** | 32 | 475 | 492 | 515 | **515.2** | 20.8 |
| **Go** | 40 | 462 | 469 | 508 | **513.5** | 37.6 |

**F#/Go: mean 1.003×, median 1.014×, p5 1.049×.** Steady-state mean and
median are at parity; F#'s steady-state variance is *lower* than Go's.
The residual is ~5 % at the p5 floor — the native-vs-managed structural
gap, small, and not worth `unsafe`-level work to chase.

## Part 8 — the micro-optimisation plan, and the real ceiling

Part 7e's "parity" was measured **F#-first, sequential**: F#'s warm-up
slow runs got dropped by the fast-cluster filter, Go ran second fully
warm. A **50-run alternating** protocol (`fs,go,fs,go,…`, both pinned —
the fair comparison, it kills the ordering artifact) showed F# ~16 ms
*behind* on the headline `ms`. So "parity" was never real. The plan:
three more ideas to try to dip below ch-go. **The real outcome was a
diagnosis, not a speedup — and it's better news than the headline.**

### 8a. Idea 2 — `max_block_size` sweep: rejected

Hypothesis: bigger blocks → fewer per-block headers → less parse
overhead. Tested 65536 / 131072 / 262144. The result was the
*opposite* — driver time *rose* with block size (≈ 379 / 507 / 501 ms).
The driver is **wait-bound** — blocked in `read(2)` for the server to
produce the next block — so a bigger block just means the server takes
longer to produce each one, and that dominates. 65536 stays default.

### 8b. Idea 3 — Reader owns its buffer: done, correct, perf-neutral

`Reader` wrapped a `BufferedStream` over `BlockingFdStream`; every
header byte went `Byte()` → `ReadFull(1-byte span)` → virtual
`Stream.Read`. Rewrote it: `Reader` owns an 8 KB `byte[]` + `pos`/`len`,
so `Byte()` / `UVarInt()` / fixed-int reads index the buffer directly —
no virtual call, no `BufferedStream` layer. `CompressedStream` was
refactored to pull raw bytes back through the Reader via a new
`IRawByteSource` interface, so the plain and compressed paths share one
underlying byte stream.

202 Expecto tests + the `--ping` / `--mixed` / `--insert` /
`--mixed --lz4` smokes all pass. Measured: **perf-neutral** — within the
noise floor, matching the plan's ~2 ms estimate. Kept anyway: it is the
cleaner architecture (one fewer stream layer, index-based header reads),
and was the intended foundation for Idea 1 — which 8c made moot.

### 8c. The diagnosis — instrument both `sum`/`driver` splits

`bench/fs` already split its timed region into `sum` (the `OnBlock`
client-side sum — bench scaffolding) and `driver`. Added the **same
split to `bench/go`**. 50-run alternating, clean (0 drops both sides):

| | fs | go |
|---|---|---|
| `ms` (total) | 496 | 477 |
| `driver` | **350** | **376** |
| `sum` | 146 | 101 |

**The F# driver is ~26 ms *faster* than ch-go's** — and not by luck: F#
wins at min (316 vs 345), p5, median and mean, with *lower* variance
(stdev 18.6 vs 29.8). The ~19 ms by which F# "loses" the headline is
**entirely the `sum` scaffolding** (146 vs 101); the arithmetic closes
exactly (`−45 sum + 26 driver = −19`). So **Idea 1** — a zero-copy
column-window refactor to make the *driver* faster — is **moot**: the
driver already wins. Idea 1 was dropped without implementing it.

### 8d. The `sum` loop — SIMD works, but the workload is server-paced

Idiomatic F# `for i in 0..n-1 do s <- s + span.[i]` ran the sum at
~26 GB/s; the `n` trip count (not `span.Length`) leaves a bounds check
that blocks RyuJIT's autovectoriser. An explicit `Vector<uint64>`
reduction is a real **1.75× compute win** — the block is L2-resident
(512 KB in 2 MB L2), *not* DRAM-bandwidth-bound, which is exactly why
SIMD helps here when Part 6's DRAM-bound microbench only saw +6 %.

But it does **nothing for the headline.** A `CH_SUM` env toggle drove a
50-run *interleaved* A/B — same binary, scalar vs vector, so
server-state cancels:

| | scalar | vector |
|---|---|---|
| `ms` | **485.9** | **487.1** |
| `sum` | 144 | 82 |
| `driver` | 341 | 405 |
| `cpu` | 40 | 39 |

`sum −62`, `driver +63`, `cpu` unchanged, `ms` **identical**. It is not
AVX throttling — the client compute (`cpu`) didn't move. It is pure
accounting: the workload is **server-production-bound**, the client is
faster than ClickHouse can feed it, so a 62 ms-faster sum just spends
62 ms more blocked in `read(2)`. The Vector loop was **reverted** — no
headline benefit, only bench complexity.

### 8e. The real result

| | F# | ch-go |
|---|---|---|
| driver | ~341–350 ms | ~375 ms |
| `sum` (scaffolding) | ~145 ms | ~101 ms |
| headline `ms` | ~486 ms | ~477 ms |
| `ms` min (best run) | ~454 | ~448 |

**The F# driver beats ch-go's by ~25–35 ms.** The headline `ms` sits
~2 % behind because the F# `sum` *scaffolding* is slower **and** the
workload is server-paced, so that can't be hidden — and the sum is not
driver code. Best-run times effectively tie. This is the real ceiling:
**server-production-bound, no client-side lever remains.** The driver —
the part this project actually owns — is ahead.

## Conclusion

1. Started ~14 % behind ch-go (and the first `bench.py` numbers were a
   further ~10 pp inflated by timing process-wall — .NET startup).
2. The 3× run-to-run variance was never a driver property — it is
   P-core/E-core scheduling on a shared box (Part 1).
3. Two committed changes closed the gap: **synchronous connect**
   (`7df7360`, killed the epoll busy-loop / `sched_yield` storm) and
   **fast-path decode** (`c48b6c5`, killed the per-block
   re-validation).
4. End state (Part 8 supersedes the Part 7e "parity" claim): the **F#
   driver beats ch-go's by ~25–35 ms** (clean alternating + interleaved
   A/Bs). The headline wall time is ~2 % behind only because the
   bench's `sum` scaffolding is slower *and* the workload is
   server-paced — not a driver gap. Best-run times tie.
5. Things that sounded good but didn't pay: `Unsafe.Add` (refuted,
   −11 % — .NET 10 RyuJIT already BCEs + vectorises sequential loops),
   the "free L2 win" (false premise — both sides already zero-copy),
   `SO_RCVBUF`/`Blocking` (no effect — .NET's epoll engine ignores
   them), Workstation GC (no effect), `max_block_size` (Idea 2 —
   *worse*, the driver is wait-bound), and a `Vector<uint64>` sum loop
   (a real 1.75× on the sum, but the workload is server-paced so the
   headline `ms` didn't move — reverted).
6. Idea 1 (zero-copy column windows) was **dropped without
   implementing** — 8c proved the F# driver already leads, so there is
   no driver gap for it to close.

## What's still open (low priority)

- **The bimodal slow path.** Fully killing it needs the ClickHouse
  server pinned off the client's cores (disjoint core sets). The
  server is shared infra (another user's process), so this is a
  human call, not a code change. Pinning the *client* to P-cores is
  enough for a reproducible steady-state measurement.
- **`perf` access.** `perf_event_paranoid` is 4; a real cycle-level
  profile needs `sudo sysctl kernel.perf_event_paranoid=1`. We never
  needed it — syscall counting + the driver/sum split were enough —
  but it would be the tool if the p5 residual is ever worth chasing.
- **The p5 ~5 % floor.** Native vs managed. Closing it would mean
  `unsafe`-level reinterpret tricks for a few ms on the best run —
  not worth it against the maintainability cost.

## Repo state (committed)

```
7df7360  Eliminate the socket-engine busy-loop in the receive path
         — BlockingFdStream, sync connect+handshake, 8 KB BufferedStream,
           Buf.WriteToAndReset, bench/fs harness
c48b6c5  Fast-path block decode: validate the schema once, not every block
         — Reader.SkipStr, Block.decodeFast, hoisted receive-loop closures
f00fe46  bench/fs: split the OK line into driver-time vs sum-time
```

## Working tree (Part 8 — uncommitted as of writing)

- `src/Ch.Proto/Reader.fs` — rewritten: Reader owns an 8 KB buffer,
  index-based small reads (Idea 3, 8b).
- `src/Ch.Proto/CompressedStream.fs` — `IRawByteSource` refactor; takes
  the Reader as its raw-byte source instead of an inner `Stream`.
- `src/Ch.Client/Client.fs` — `Reader` is built over `BlockingFdStream`
  directly; the `BufferedStream` layer is gone.
- `bench/go/main.go` — `sum`/`driver` split on the OK line (8c).
- `bench/fs/Program.fs` — `CH_BLOCK_SIZE` sweep knob (8a) + read/cpu
  split on the OK line. The `Vector<uint64>` sum loop (8d) was tried
  and reverted; the loop here is the original scalar one.
- `scripts/measure-ab.py`, `scripts/measure-sumloop.py` — the
  alternating and interleaved A/B harnesses.
