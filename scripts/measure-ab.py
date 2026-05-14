#!/usr/bin/env python3
"""A/B measurement for the perf plan.

Alternates fs/go (kills the ordering/warm-up artifact), pins both to the
P-cores (taskset -c 0-15), parses each program's *own* self-reported time
(not subprocess wall — that includes ~200 ms .NET startup), and reports
fast-cluster stats. Runs >= 800 ms are P/E-core contention, not the
driver, so they are dropped from the cluster.

Both benches print the same split:
  OK: ... | <ms> ms | ... | sum <s> | driver <d>[ (read .. cpu <c>)]
`driver = ms - sum` is the apples-to-apples figure: it strips the sum
callback, which is bench scaffolding, not driver work. (fs additionally
breaks driver into read(2) vs cpu; go can't without patching ch-go.)
"""
import re
import statistics
import subprocess
import sys
import time

FS = ["taskset", "-c", "0-15", "dotnet", "/tmp/ch-bench-numbers-fs/Bench.Numbers.dll"]
GO = ["taskset", "-c", "0-15", "/tmp/ch-bench-numbers-go"]
RUNS = 50
PAUSE = 3.0
FAST_CLUSTER_MAX = 800.0

ms_re = re.compile(r"\| (\d+) ms \|")
sum_re = re.compile(r"sum (\d+)")
driver_re = re.compile(r"driver (\d+)")
cpu_re = re.compile(r"cpu (\d+)\)")


def run(cmd):
    p = subprocess.run(cmd, capture_output=True, text=True)
    if p.returncode != 0:
        sys.exit(f"command failed: {' '.join(cmd)}\n{p.stdout}\n{p.stderr}")
    return p.stderr


def parse_fs(out):
    return (
        int(ms_re.search(out).group(1)),
        int(sum_re.search(out).group(1)),
        int(driver_re.search(out).group(1)),
        int(cpu_re.search(out).group(1)),
    )


def parse_go(out):
    return (
        int(ms_re.search(out).group(1)),
        int(sum_re.search(out).group(1)),
        int(driver_re.search(out).group(1)),
    )


fs_ms, fs_sum, fs_driver, fs_cpu = [], [], [], []
go_ms, go_sum, go_driver = [], [], []
for i in range(1, RUNS + 1):
    m, s, d, c = parse_fs(run(FS))
    fs_ms.append(m)
    fs_sum.append(s)
    fs_driver.append(d)
    fs_cpu.append(c)
    time.sleep(PAUSE)
    gm, gs, gd = parse_go(run(GO))
    go_ms.append(gm)
    go_sum.append(gs)
    go_driver.append(gd)
    time.sleep(PAUSE)
    print(
        f"  run {i:2d}/{RUNS}: fs ms={m} sum={s} driver={d} cpu={c} "
        f"| go ms={gm} sum={gs} driver={gd}",
        flush=True,
    )


def stats(label, xs):
    fast = sorted(x for x in xs if x < FAST_CLUSTER_MAX)
    dropped = len(xs) - len(fast)
    if not fast:
        print(f"{label}: no fast-cluster samples")
        return
    n = len(fast)
    if n >= 2:
        p5 = statistics.quantiles(fast, n=20)[0]
        sd = statistics.stdev(fast)
    else:
        p5, sd = fast[0], 0.0
    print(
        f"{label:14s} n={n:2d} (drop {dropped})  "
        f"min={fast[0]:4d}  p5={p5:6.1f}  median={statistics.median(fast):6.1f}  "
        f"mean={statistics.mean(fast):6.1f}  stdev={sd:5.1f}"
    )


print()
print(f"=== fast cluster (< {FAST_CLUSTER_MAX:.0f} ms), {RUNS} runs alternating fs/go ===")
stats("fs ms", fs_ms)
stats("go ms", go_ms)
stats("fs driver", fs_driver)
stats("go driver", go_driver)
stats("fs sum", fs_sum)
stats("go sum", go_sum)
stats("fs cpu", fs_cpu)

with open("/tmp/measure-ab-raw.txt", "w") as f:
    f.write("run\tfs_ms\tfs_sum\tfs_driver\tfs_cpu\tgo_ms\tgo_sum\tgo_driver\n")
    for i in range(RUNS):
        f.write(
            f"{i + 1}\t{fs_ms[i]}\t{fs_sum[i]}\t{fs_driver[i]}\t{fs_cpu[i]}\t"
            f"{go_ms[i]}\t{go_sum[i]}\t{go_driver[i]}\n"
        )
print("\nraw samples -> /tmp/measure-ab-raw.txt")
