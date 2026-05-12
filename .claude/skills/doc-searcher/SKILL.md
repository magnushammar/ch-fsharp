---
description: Use whenever looking up docs, code references, syntax, or usage examples for ANY indexed library or system — ClickHouse, language libraries, internal collections, project docs. Required before falling back to grep or pre-training knowledge. Defines how to use the `doc-searcher` MCP's 11 tools — semantic search first, raw-file access only as escape hatch. Domain-specific skills like `clickhouse-expert` layer collection-specific knowledge on top of this workflow.
---

# doc-searcher tool reference

The `doc-searcher` MCP indexes documentation and code from multiple collections (ClickHouse docs and any others — run `list_collections` to discover). It exposes **semantic vector search** as the primary interface and **raw file access** (fd/rg/cat wrappers) as an escape hatch.

## Core rule

**Always semantic-search first. Raw-file tools are the escape hatch, not the front door.**

Embeddings find paraphrase-equivalent content; grep only finds literal matches. A doc that says "calculates pairwise sample covariance" is invisible to a grep for `covarSamp` but surfaces immediately under `doc_search("sample covariance")`.

## The 11 tools, ordered by usage

### Phase 1 — semantic search (Discovery)

These return chunk metadata + IDs. They do **not** return full content; call `get_chunks` next.

**`doc_search(query, collection?, kind?, level?, limit?)`**
- Semantic search over documentation chunks. Use for "what is X", "how does Y work", concept lookups.
- `kind` filters by content type: `"concept"`, `"api"`, `"example"`, `"guide"`, `"reference"`. Useful — set `kind="example"` to find usage examples, `kind="api"` for signatures.
- `level` filters by hierarchy depth: `"document"`, `"section"`, `"paragraph"`. Use `"section"` for medium-grained results, `"paragraph"` for fine-grained.
- `limit` 1–20, default 10.

**`code_search(query, collection?, kind?, language?, contentType?, package?, limit?)`**
- Semantic search over source code. Use for "find the implementation of X", function signatures, type definitions.
- `kind` filters by code type: `"function"`, `"class"`, `"method"`, `"interface"`, `"type"`, `"component"`, `"hook"`, `"group"`.
- `language` filters by language: `"typescript"`, `"python"`, etc.
- `contentType` filters by role: `"src"`, `"test"`, `"example"`. Set `"test"` to learn from test cases, `"example"` for sanctioned demo code.
- `package` filters by package name.

**`hybrid_search(query, collection?, type?, limit?)`**
- Searches both code AND docs simultaneously. Use when the topic could legitimately be in either (e.g. "how do I use Effect.catchTag" — could be docs explanation or code example).
- `type` controls scope: `"code"`, `"docs"`, `"both"` (default).

### Phase 2 — retrieve full content

**`get_chunks(ids)`**
- Fetches full content for chunk IDs returned by Phase 1. Up to 50 IDs per call — batch retrievals to save round-trips.
- This is where you actually read docs. Search results are snippets and metadata only.

**`get_chunk_context(repository, filepath, startLine, linesBefore?, linesAfter?)`**
- **DEPRECATED.** Use `get_chunks` instead. Mentioned only so you don't waste time on it.

### Phase 3 — navigation

Use these to understand structure when a search points you at a chunk inside a larger doc.

**`get_document_overview(documentId)`**
- Shows the section/paragraph tree of a document without fetching content. Use when one chunk hit looks promising and you want to know what else is in the same doc.

**`get_related_overview(chunkId, relation)`**
- Navigate the hierarchy. `relation` is one of:
  - `"parent"` — one level up
  - `"children"` — direct descendants
  - `"siblings"` — same parent
  - `"ancestors"` — all parents up to root

### Phase 4 — discovery

**`list_collections()`**
- No args. Lists all indexed collections with their sizes. Run once at session start if you don't know which collection covers your topic.

### Phase 5 — escape hatches (raw file access)

Use only when semantic search has truly failed (0 useful results from `doc_search` AND `hybrid_search` after reformulating the query).

**`list_files(collection, args)`** — `fd` wrapper. Args like `"-e md -g '*MergeTree*'"`. Use to discover file layout.

**`grep_files(collection, args)`** — `rg` (ripgrep) wrapper. Args like `"-i -A 3 'pattern' -g '*.json'"`. Last resort for literal-pattern lookups.

**`read_raw_file(collection, filepath, flags?)`** — `cat` wrapper, with optional `flags="-n"` for line numbers. Use when you have a known doc path from external context.

## Workflow patterns

**Standard lookup** (concept, syntax, or example):
1. `doc_search(query, collection)` (optionally with `kind="example"` if you want examples specifically)
2. Inspect top 3–5 results' snippets + scores
3. `get_chunks(ids)` with the promising IDs to read the actual content
4. Stop.

**Code-implementation lookup** (find a function, see how it's used):
1. `code_search(query, collection, kind="function")` (or "method", "class")
2. `get_chunks(ids)` on the top hit(s)
3. Optionally `get_related_overview(chunkId, "siblings")` to see related code

**"Where is this documented at all"** (unsure if doc exists):
1. `hybrid_search(query)` for the broadest match
2. If 0 useful results: try a 2nd query with synonyms or different framing
3. If still nothing: `list_collections()` to confirm there's even a collection for this topic
4. Last resort: `grep_files(collection, "<likely literal pattern>")`

**Surveying a topic** (want to read multiple related docs):
1. `doc_search(query)` for entry points
2. `get_document_overview(documentId)` on the top hit to see what's available in that doc
3. `get_chunks` on multiple sections from the overview

## Anti-patterns

- **Treating doc-searcher like grep**: skipping semantic search loses paraphrase-equivalent matches.
- **Skipping `get_chunks`**: search returns snippets only. The actual content is one tool call away.
- **Calling `get_chunks` one ID at a time**: batch up to 50.
- **Ignoring `kind` / `language` / `contentType` filters**: they massively improve precision.
- **Not passing `collection=`**: leaves the search unscoped across all collections — slower and noisier.
- **Going straight to `grep_files`**: misses content that uses different terminology than your query. Reformulate the semantic query before falling back.
- **Trusting `get_chunk_context`**: it's deprecated. Use `get_chunks` (with the same chunk ID from search results).

## Reformulation before escape

If semantic search returns 0 useful results for something you genuinely need:

1. **Try a synonym** — e.g. "sample variance" → "unbiased variance estimator" → "variance n-1 normalisation".
2. **Try the symbol name directly** — many docs include the symbol verbatim in headings.
3. **Try `hybrid_search`** — different scoring may surface results `doc_search` ranks lower.
4. **Try `kind` / `level` filters** — e.g. drop the `level` filter to broaden the search.
5. **Only then** fall back to `grep_files` with a likely literal pattern.

Most "the docs don't have this" failures are actually query-formulation failures.

## Layering with domain skills

Domain-specific skills (e.g. `clickhouse-expert`) carry collection-specific knowledge: which collection covers their domain, which query terms work best, common pitfalls, project-specific table schemas. They are NOT alternatives to this skill — they layer on top of the workflow defined here.
