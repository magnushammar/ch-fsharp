#!/usr/bin/env python3
"""Scalar vs Vector sum-loop A/B — same fs binary, CH_SUM env toggles.

Interleaves CH_SUM=scalar and CH_SUM=vector on the *same* published fs
binary, so server-state / machine noise cancels. Answers definitively:
does the Vector<uint64> reduction help the headline `ms`, and does it
slow the adjacent `driver` code (the cross-session puzzle)?
"""
import os
import re
import statistics
import subprocess
import sys
import time

DLL = "/tmp/ch-bench-numbers-fs/Bench.Numbers.dll"
BASE = ["taskset", "-c", "0-15", "dotnet", DLL]
RUNS = 50
PAUSE = 3.0
FAST_CLUSTER_MAX = 800.0

ms_re = re.compile(r"\| (\d+) ms \|")
sum_re = re.compile(r"sum (\d+)")
driver_re = re.compile(r"driver (\d+)")
cpu_re = re.compile(r"cpu (\d+)\)")


def run(sum_mode):
    env = {**os.environ, "CH_SUM": sum_mode}
    p = subprocess.run(BASE, capture_output=True, text=True, env=env)
    if p.returncode != 0:
        sys.exit(f"command failed (CH_SUM={sum_mode}):\n{p.stdout}\n{p.stderr}")
    out = p.stderr
    return (
        int(ms_re.search(out).group(1)),
        int(sum_re.search(out).group(1)),
        int(driver_re.search(out).group(1)),
        int(cpu_re.search(out).group(1)),
    )


cols = {k: [] for k in ("sc_ms", "sc_sum", "sc_driver", "sc_cpu",
                        "ve_ms", "ve_sum", "ve_driver", "ve_cpu")}
for i in range(1, RUNS + 1):
    m, s, d, c = run("scalar")
    cols["sc_ms"].append(m); cols["sc_sum"].append(s)
    cols["sc_driver"].append(d); cols["sc_cpu"].append(c)
    time.sleep(PAUSE)
    m, s, d, c = run("vector")
    cols["ve_ms"].append(m); cols["ve_sum"].append(s)
    cols["ve_driver"].append(d); cols["ve_cpu"].append(c)
    time.sleep(PAUSE)
    print(
        f"  run {i:2d}/{RUNS}: scalar ms={cols['sc_ms'][-1]} sum={cols['sc_sum'][-1]} "
        f"driver={cols['sc_driver'][-1]} | vector ms={cols['ve_ms'][-1]} "
        f"sum={cols['ve_sum'][-1]} driver={cols['ve_driver'][-1]}",
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
        f"{label:16s} n={n:2d} (drop {dropped})  "
        f"min={fast[0]:4d}  p5={p5:6.1f}  median={statistics.median(fast):6.1f}  "
        f"mean={statistics.mean(fast):6.1f}  stdev={sd:5.1f}"
    )


print()
print(f"=== fast cluster (< {FAST_CLUSTER_MAX:.0f} ms), {RUNS} runs interleaved scalar/vector ===")
stats("scalar ms", cols["sc_ms"])
stats("vector ms", cols["ve_ms"])
stats("scalar sum", cols["sc_sum"])
stats("vector sum", cols["ve_sum"])
stats("scalar driver", cols["sc_driver"])
stats("vector driver", cols["ve_driver"])
stats("scalar cpu", cols["sc_cpu"])
stats("vector cpu", cols["ve_cpu"])

with open("/tmp/measure-sumloop-raw.txt", "w") as f:
    keys = ("sc_ms", "sc_sum", "sc_driver", "sc_cpu",
            "ve_ms", "ve_sum", "ve_driver", "ve_cpu")
    f.write("run\t" + "\t".join(keys) + "\n")
    for i in range(RUNS):
        f.write(f"{i + 1}\t" + "\t".join(str(cols[k][i]) for k in keys) + "\n")
print("\nraw samples -> /tmp/measure-sumloop-raw.txt")
