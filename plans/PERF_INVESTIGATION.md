# Performance Investigation — F# driver vs ch-go

Status: **in progress, not concluded.** This document records every
hypothesis tried, what the evidence showed, and what is still open. All
work is on uncommitted changes on `main` (see "Repo state" at the end).

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

## What we know for certain

1. The 3× run-to-run variance is P-core/E-core scheduling. Pinning the
   client to P-cores makes it reproducible.
2. With both pinned, F# is a stable **~14 %** behind ch-go.
3. F# burns ~200 K `sched_yield` from threadpool worker spin. It is
   real overhead but its wall-time cost is **not yet isolated** — the
   one clean A/B (the env var) didn't clearly move the needle.
4. `recvmsg`/`epoll`/`read` counts are *not* the differentiator — F#
   and Go do comparable I/O syscall volumes.
5. The client decode is **~memory-bandwidth-bound on one P-core**
   (~17 of 24 GB/s). The `BufferedStream` double-copy is ~11 % of that
   traffic — the biggest *addressable* slice.
6. .NET 10 RyuJIT autovectorises + BCEs simple sequential loops;
   hand-written `Unsafe.Add` regresses them.

## Open problems / what I'd do next

### The benchmark environment is still not clean
`taskset -c 0-15` pins the *client*, but the ClickHouse server's ~24
threads also float onto 0–15 and crowd the decode thread. To get truly
stable numbers the **server and client must be on disjoint core sets** —
e.g. pin the server to E-cores (`taskset -c 16-23` on the server
process, or a systemd `CPUAffinity`/cgroup) and the client to a couple
of P-cores. Without that, every heavy A/B drifts into slow-mode and the
signal drowns. This is the single biggest blocker to further progress.

### Need a real CPU profile
`perf stat` / `perf record` are blocked — `perf_event_paranoid` is 4,
needs `sudo sysctl kernel.perf_event_paranoid=1` (or `-1`). Without it
we are syscall-counting, which found the `sched_yield` storm but can't
show where *cycles* go. With perf we could see the cycles/instruction
split, cache-miss rate, and the actual hot functions — that is the
direct path to the remaining ~70 ms.

### Hypotheses still on the table for the ~14 %

- **The `BufferedStream` double-copy** (4f). Strong hypothesis, just
  noise-blocked. The fix is to drop `BufferedStream` from the bulk path:
  give `Reader` a small (~8–16 KB) internal buffer for the varint/string
  header reads, and have `ReadFull` of a large span read **directly**
  into the destination column buffer with zero intermediate copy. This
  is how ch-go effectively behaves (its `bufio.Reader` bypasses its
  buffer for large reads).

- **`MemoryMarshal.Cast<byte,uint64>` vs Go's `unsafe.Pointer`
  slice-header rewrite.** Both are zero-copy reinterprets; .NET's may
  carry a bounds-check or a span-construction cost per block. Likely a
  few % at most. Logged in `DESIGN_CHOICES.md §4` as an accepted
  departure — but worth measuring once perf is available.

- **The `OnBlock` sum loop.** `for i in 0 .. n-1 do s <- s + span.[i]`
  — check the JIT actually elides the `Span` bounds check and
  vectorises. If not, an explicit `Vector<uint64>` sum or a checked
  `AsSpan()` slice would help. (The sum is bench-specific scaffolding,
  not driver code — but it's inside the timed region on both sides, so
  it must be apples-to-apples with Go's `for _, v := range data`.)

- **The `sched_yield` storm, properly A/B'd.** Re-test
  `UnfairSemaphoreSpinLimit=0` with server/client on disjoint cores. If
  it then *does* move wall-time, the proper fix is to stop the receive
  loop from ever touching the threadpool: confirm `DoAsync` truly runs
  inline (it may still hop — 4d was inconclusive), and consider running
  the whole query on a dedicated `Thread` with `IsBackground=true`
  rather than through the `task` builder at all.

- **Per-block fixed overhead.** ~7,630 blocks each parse a BlockInfo
  tag-loop + varints byte-by-byte through `Reader.Byte()` →
  `ReadFullInto(1-byte span)`. Each 1-byte read is a virtual `Stream.Read`
  call. ch-go reads varints straight from the `bufio.Reader`'s buffer.
  Worth collapsing `Reader`'s small reads onto a direct buffer-index
  path instead of going through the `Stream` abstraction per byte.

### Realistic expectation
ch-go is native, hand-tuned, and uses `unsafe`. Some gap is structural.
But ~14 % is not all structural — the `BufferedStream` double-copy and
the per-byte `Stream.Read` for varints are both addressable in managed
code. A realistic target is **single-digit %**; matching or beating
ch-go would require the same `unsafe`-level reinterpret tricks and is a
stretch goal, not a baseline expectation.

## Repo state (uncommitted, on `main`)

```
 M src/Ch.Client/Client.fs        BlockingFdStream wiring; sync DoAsync flush;
                                  SO_RCVBUF 16 MB
 M src/Ch.Proto/Buffer.fs         + Buf.WriteToAndReset (sync)
 M src/Ch.Proto/Ch.Proto.fsproj   + BlockingFdStream.fs in compile order
?? src/Ch.Proto/BlockingFdStream.fs   new — blocking read(2)/write(2) Stream
?? bench/fs/                      new — minimal ch-bench-official-style harness
```

None of this is committed yet — it's a checkpoint of the investigation,
not a finished change. `BlockingFdStream` + the sync `DoAsync` flush are
worth keeping regardless (they match Go's I/O model and remove a
threadpool hop); the rest depends on what the next round — with a clean
environment and `perf` — turns up.
