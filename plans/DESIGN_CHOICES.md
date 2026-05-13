# Design Choices

A running log of deferred decisions and intentional departures from the
ch-go reference implementation. New entries get appended at the bottom of
their section.

The current low-level API mirrors ch-go: columnar, perf-first. A
row-oriented .NET-idiomatic layer and a CE-based layer are both intended
but not yet started; they will wrap the low-level API.

## Deferred — decisions we explicitly postponed

| Topic | Reason for deferring |
|---|---|
| **LC(FixedString)** | Needs `IEqualityComparer<byte[]>` for content-hashing in the write-side dedup `Dictionary`. No concrete use case yet — ship LC(String) and LC(numeric) only. |
| **LC of composite types** (Nullable, Array, …) | Recursive `IColumnOf<'T>` over composite inner. Will revisit when we implement composites. |
| **Decimal256** | Underlying Int256 not in .NET; needs a custom 32-byte struct. Decimal32/64/128 are shipped. |
| **Enum8 / Enum16** | Needs `Infer` to parse `Enum8('a'=1, 'b'=2)` DDL into a name⇆int map. |
| **Int256 / UInt256** | No .NET native type; requires a custom 32-byte struct. |
| **BFloat16** | .NET 8+ has `Half` (16-bit IEEE), but bfloat16 has a different bit layout (7-bit mantissa vs 10-bit). Needs a custom struct. |
| **INSERT path / OnInput streaming** | Single biggest piece of remaining functionality. Needs the full-duplex sender/receiver pattern. |
| **ColAuto inference** | Pleasant but not perf-critical — users can pre-declare result columns. |
| **Connection pool** | ch-go itself has it as a separate `chpool` package and explicitly disclaims it. We'll do the same when there's demand. |
| **ZSTD compression** | LZ4 covers the dominant case; ZSTD is straightforward to add via `ZstdSharp.Port` if needed. |
| **SSH auth** | Niche. |
| **OpenTelemetry instrumentation** | Decorative in ch-go; not on the perf path. |
| **Query cancellation watchdog** | ch-go has a third goroutine that fires `ClientCodeCancel` on context-cancellation. Our `Task`-based receive loop doesn't have it yet — `CancellationToken` is passed through but we don't proactively send Cancel. |
| **Query parameters / inter-server secret** | Empty strings in the Query packet are accepted by the server. |

## Departures from ch-go

### Layering / API

1. **Single `ColPrimitive<'T when 'T : unmanaged>` generic, sealed leaves per concrete type.**
   ch-go has ~100 hand-templated `col_*_gen.go` / `_safe_gen.go` / `_unsafe_gen.go`
   files. We collapse to one generic class + 10 one-line `[<Sealed>]` leaves.
   CLR generic specialisation per value type gives the same JIT-inlined,
   non-virtual hot path. ~70 % less code.

2. **Buffer growth uses `Array.Resize` (preserves contents)** instead of
   ch-go's `make + copy` idiom. Same semantics, less code.

3. **`Buf` instead of `Buffer` for the encode-side wrapper** because
   `System.Buffer` is in the BCL and shadows our type.

4. **`MemoryMarshal.Cast<byte, 'T>` for the zero-copy reinterpret** instead
   of ch-go's `unsafe.Pointer` slice-header rewrite. .NET API does the same
   thing, no `unsafe` keyword needed.

5. **No big-endian / `purego` fallback path.** ch-go has a build-tagged
   safe variant for non-LE archs. .NET 10 targets (x64, arm64, wasm) are
   all little-endian; we trust the host.

6. **F#-side .NET semantic helpers on Date/DateTime/IPv4/IPv6 columns**
   (`AppendDate(DateOnly)`, `RowDate`, `AppendIP(IPAddress)`, …) live next
   to the raw `Append(uint32)` / `Row(i): uint32`. ch-go has equivalents
   on its `Date` / `DateTime` typedefs.

7. **ColUUID uses virtual `Encode/DecodeColumn` overrides** to apply the
   two-half byte-swap. ch-go has a separate `bswapUUID` call invoked from
   the encode/decode hot path.

8. **`ColumnType.normalize` substitutes `Decimal(P, S)` → `Decimal{32,64,128,256}`**
   so the server-sent `Decimal(9, 2)` matches a client-declared `ColDecimal32`.
   Regex-based; recurses through composites (`Array(Decimal(9, 2))` →
   `Array(Decimal32)`). Mirrors ch-go's `decimalDowncast`. Same idea will
   extend to Enum8/16 once we add Enum, and DateTime timezone parameters.

9. **Decimal scale conversion is a free function (`Decimal.fromInt32` /
   `toInt32` / `fromInt64` / `toInt64`), not a member on the column.**
   ch-go uses raw `Decimal32 = int32` aliases with no scale baked in;
   we follow the same — scale is the caller's concern. Decimal128 with
   >28 digits overflows `System.Decimal`; users in that regime work with
   the raw `Int128`.

### `ColLowCardinality` — the load-bearing departure

8. **No dense `Values []T` materialisation on decode.** ch-go materialises
   a full `Values []T` of length=`rowCount` by calling `inner.Row(idx)` per
   row. For `LC(String)` with `inner = ColStr` this is **65 000 string
   allocations per 65k-row block** (each `inner.Row(idx)` does
   `Encoding.UTF8.GetString` on the same span).

   We instead materialise the **dictionary**: a `'T[] dictArray` of
   length=`dictRows` (typically ~100), filled once on decode. `Row(i)` is
   then `dictArray[readKey(i)]` — same O(1) array deref ch-go's Values has,
   but **strings allocated per block = dictRows, not rowCount**.

   Trade-off accepted: `Row(i)` pays one extra byte-read (~1 ns) on top of
   the array deref. On a 500 M-row read that's ~500 ms extra latency, in
   exchange for ~650× fewer string allocations per block — net wins on GC
   pressure, working-set, and large-block throughput.

9. **Keys storage is a single `byte[]` interpreted by `keyWidth`** (1, 2,
   4 or 8). ch-go keeps four separate typed slices (`keys8`, `keys16`,
   `keys32`, `keys64`) and a `keys []int` shadow. We only ever use one
   width at a time so the dispatch is one branch in `Row(i)` and the rest
   is straight `MemoryMarshal.Read<UInt8/16/32/64>`. Half as much code,
   same perf.

10. **`Prepare` is internal to encode** instead of a separately-exposed
    method (ch-go has `Preparable` interface). User-facing API stays
    `Append(v: 'T)` → call `EncodeColumn(buf)`; the dedup happens
    transparently. Equivalent perf, smaller surface.

### Tests / tooling

11. **xunit + `[<Theory>]` + `[<InlineData>]`** for tests instead of Go's
    `testing` package. Shared `roundtrip` helpers parameterise the
    template-style tests ch-go generates per type.

12. **Golden binary fixtures read straight from the submodule** at
    `reference/ch-go/proto/_golden/`. No duplication into our test tree.

13. **Bench harness is a Python script (`scripts/bench.py`)** mimicking
    hyperfine because hyperfine isn't installed on the bench host. ch-bench
    upstream uses `hyperfine -w 10 -r 100`.
