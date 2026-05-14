#!/usr/bin/env bash
# Smoke the Reader rewrite against a live ClickHouse on localhost:9000.
# Exercises: ping, mixed-type SELECT, INSERT round-trip, and the LZ4
# compression path (the CompressedStream refactor's main risk).
set -euo pipefail
cd "$(dirname "$0")/.."

PROJ=src/Ch.Bench.Numbers/Ch.Bench.Numbers.fsproj
run() { echo "=== $* ==="; dotnet run -c Release --project "$PROJ" -- "$@"; echo; }

run --ping
run --mixed
run --insert
run --mixed --lz4

echo "all smokes passed"
