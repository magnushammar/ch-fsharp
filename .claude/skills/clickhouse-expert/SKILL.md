---
description: Use whenever generating, modifying, reading, or reasoning about ClickHouse SQL — DDL, aggregations, window functions, ARRAY JOIN, materialized views, partitioning, FINAL semantics, argMax/argMin, time-bucketing, system tables, performance tuning — even if the user doesn't say 'ClickHouse'. Required before writing any non-trivial query against the `spot` database. Layers on top of the `doc-searcher` skill for retrieval; this skill carries the ClickHouse-specific knowledge (which collection, query terms that work, common pitfalls, anti-patterns).
---

# ClickHouse retrieval guide

You are working with ClickHouse-flavoured SQL. The `clickhouse-docs` collection in the `doc-searcher` MCP has all the syntax, function signatures, and engine behaviour you'll need.

**Use the search hierarchy from the `doc-searcher` skill** — semantic-first, grep as escape hatch. This skill carries only the ClickHouse-specific pieces.

## Verification rule

ClickHouse syntax drifts across versions and the standard library is large. Before writing any non-trivial CH SQL — DDL, window functions, ARRAY JOIN, FINAL semantics, JSON columns, MergeTree DDL settings, array-combinator stacks — verify the relevant function or pattern against the docs via `doc-searcher`. Do **not** rely on pre-training knowledge of ClickHouse.

"Non-trivial" means: a function or pattern you haven't already verified in this session. Trivial reuse of established patterns from elsewhere in the repo is fine — but trace those patterns back to a verified source if there's any doubt.

## Collection

- `clickhouse-docs` — combined reference + guides (~13,800 chunks).
  - **Reference docs**: SQL syntax, functions, data types, engines, system tables.
  - **User guides**: getting started, integrations, cloud, tutorials, knowledgebase.

If you're unsure whether the docs cover your topic, run `mcp__doc-searcher__list_collections` once and confirm.

## Query terms that work well

ClickHouse docs use specific language. These query shapes consistently surface useful results in `doc_search`:

- **Function lookup**: search the camelCase function name directly. `"covarSamp"`, `"arrayDifference"`, `"lagInFrame"`, `"uniqExact"`.
- **Aggregate combinators**: search `"-If combinator"`, `"-State combinator"`, `"-Array combinator"`, etc.
- **Window functions**: search `"window function <name>"` or `"OVER PARTITION BY"` for the syntax overview.
- **Engine behaviour**: search `"<engine name> table engine"`, e.g. `"ReplacingMergeTree"`.
- **FINAL semantics**: search `"FINAL modifier"` — important because `FINAL` is rarely correct and often misused.
- **JSON / Tuple / Map operations**: search the type name first, then the operation.
- **Time bucketing**: search `"intDiv timestamp bucket"` or `"toStartOfInterval"`.

If a search returns 0 useful results, try `hybrid_search` next (broader scope). Only fall back to `grep_files` when both semantic searches fail with reformulated queries.

## Common ClickHouse-specific pitfalls

These trip people up. When you encounter them, verify against `doc-searcher` rather than guessing.

- **`countDistinct` is approximate** (HyperLogLog, ~1–2% error). Use `uniqExact` for exact counts; use `count()` with `GROUP BY` + `HAVING` for exact duplicate checks.
- **`FINAL` is rarely needed**: historical data on `spot.trades` and its materialized klines is fully merged. Don't add `FINAL` reflexively.
- **Alias shadowing inside aggregates**: `SELECT sum(x) AS x` alongside another `sum(x)` reference triggers `ILLEGAL_AGGREGATION`. Fix with `SETTINGS prefer_column_name_to_alias = 1`, rename the alias, or use a subquery.
- **Output `Bool` on the chfs wire**: chfs has no Bool type. Cast `toUInt8(...)` in projections and unpack with `ReadUInt8 <> 0uy` in F#. Includes synthesised placeholders in UNION subqueries (`toUInt8(0)` instead of `false`).
- **`argMin` / `argMax` argument order**: `argMin(value_to_return, ordering_key)`. The `arg` can be a tuple (e.g. to lock two values to the same row); the `val` supports tuples for tie-breaking.
- **`lagInFrame` is not `lag`**: it respects the window frame. For full SQL `lag` behaviour use `ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING`. Pass a default value as the third arg if you need a known sentinel for the first row of each partition (then filter with `row_number() > 1` or a NaN check).
- **`varSamp` / `corr` numerical stability**: standard forms use a "numerically unstable" algorithm. For most universe-scale work this is fine; `varSampStable` / `corrStable` exist if accuracy is in question.
- **Array-combinator stacks** (e.g. `arrayReduce('quantile(0.95)', arrayMap(x -> ..., arrayDifference(xs)))`): verify each function and the composition shape via `doc_search`. The signature conventions differ from most other SQL flavours.

## Anti-patterns

- **Don't probe the live server for syntax**: "does this compile?" goes through `doc-searcher`, not `clickhouse-client`. See `.claude/rules/clickhouse.md`.
- **Don't grep the docs as a first move**: use `doc_search` first per the `doc-searcher` skill hierarchy. Grep misses paraphrase-equivalent content.
- **Don't trust pre-training on CH-specific function names**: the standard library is large and version-drifting. Verify.

## Project-specific context

- Repo conventions for ClickHouse SQL live in `.claude/rules/clickhouse.md` (auto-loaded on `.sql`, `.fs`, `.fsx` edits). It documents the alias-shadow gotcha, the chfs Bool-cast requirement, and the `spot` database table schemas.
- **`spot.trades`** — individual trade rows for ~443 *USDT symbols over the project window. The 10 highest-volume majors (BTC/ETH/BNB/SOL/XRP/DOGE/ADA/SHIB/FLOKI/BONK) are deliberately excluded per the repo-root `CLAUDE.md`.
- **`spot.orderbook_depth20`** — L20 book snapshots, `ReplacingMergeTree` ordered by `(symbol, epoch_ms, last_update_id)`. `epoch_ms` on this table is the Binance event time (inherited from the paired diff at ingest), not the logging-server clock. The dedup key collapses same-Binance-update rows across Tokyo + Singapore collectors.
- **`spot.klines_*` cascade** (100ms / 1s / 10s) — filtered to the curated 50-symbol active universe (different from the 443-symbol trades universe). Slim 18-column schema (Plan M v2). Skunkworks universe work uses `spot.trades` directly to reach the broader universe.
