#!/usr/bin/env python3
"""Mini-hyperfine. Runs each command N times after W warmups and reports
mean/min/max/std in milliseconds. Discards stdout/stderr."""
import argparse
import shlex
import statistics
import subprocess
import sys
import time
from pathlib import Path


def run_once(cmd: list[str]) -> float:
    t0 = time.perf_counter()
    proc = subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    elapsed = (time.perf_counter() - t0) * 1000.0
    if proc.returncode != 0:
        sys.exit(f"command failed (exit {proc.returncode}): {' '.join(cmd)}")
    return elapsed


def bench(label: str, cmd: list[str], warmups: int, runs: int, sleep_s: float) -> dict:
    for _ in range(warmups):
        run_once(cmd)
        if sleep_s > 0:
            time.sleep(sleep_s)
    samples = []
    for _ in range(runs):
        samples.append(run_once(cmd))
        if sleep_s > 0:
            time.sleep(sleep_s)
    return {
        "label": label,
        "mean": statistics.mean(samples),
        "min": min(samples),
        "max": max(samples),
        "stdev": statistics.stdev(samples) if len(samples) > 1 else 0.0,
        "samples": samples,
    }


def fmt_ms(ms: float) -> str:
    return f"{ms:.1f} ms"


def main():
    p = argparse.ArgumentParser()
    p.add_argument("-w", "--warmups", type=int, default=3)
    p.add_argument("-r", "--runs", type=int, default=10)
    p.add_argument("-s", "--sleep", type=float, default=0.5,
                   help="Seconds to sleep between runs (let server idle).")
    p.add_argument("-o", "--output", type=Path, default=None,
                   help="Append a markdown table to this file")
    p.add_argument("commands", nargs="+", help="label=cmd pairs, e.g. fs=./bin go=./bin")
    args = p.parse_args()

    results = []
    for item in args.commands:
        if "=" not in item:
            sys.exit(f"expected label=cmd, got {item!r}")
        label, _, cmd_str = item.partition("=")
        cmd = shlex.split(cmd_str)
        print(f"# {label}: {' '.join(cmd)}", flush=True)
        print(f"  warmup x{args.warmups}, measure x{args.runs}, sleep {args.sleep}s", flush=True)
        results.append(bench(label, cmd, args.warmups, args.runs, args.sleep))

    # Sort by mean ascending
    results.sort(key=lambda r: r["mean"])
    fastest = results[0]["mean"]

    headers = ["Label", "Mean (ms)", "Min", "Max", "Stdev", "vs fastest"]
    rows = []
    for r in results:
        ratio = r["mean"] / fastest
        rows.append([
            r["label"],
            f"{r['mean']:.1f}",
            f"{r['min']:.1f}",
            f"{r['max']:.1f}",
            f"{r['stdev']:.1f}",
            "1.00x" if r is results[0] else f"{ratio:.2f}x",
        ])

    widths = [max(len(h), max(len(r[i]) for r in rows)) for i, h in enumerate(headers)]
    def fmt_row(cells):
        return "| " + " | ".join(c.ljust(w) for c, w in zip(cells, widths)) + " |"
    sep = "|" + "|".join("-" * (w + 2) for w in widths) + "|"
    print()
    print(fmt_row(headers))
    print(sep)
    for r in rows:
        print(fmt_row(r))

    if args.output is not None:
        with args.output.open("a") as f:
            f.write("\n")
            f.write(fmt_row(headers) + "\n")
            f.write(sep + "\n")
            for r in rows:
                f.write(fmt_row(r) + "\n")
        print(f"\nAppended to {args.output}")


if __name__ == "__main__":
    main()
