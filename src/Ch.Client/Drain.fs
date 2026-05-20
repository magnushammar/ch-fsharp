/// Tier 2 — materialised drain helpers ("the slow path").
///
/// For consumers that need random-access to the full SELECT result,
/// not just block-by-block streaming. Memory footprint: O(total rows
/// × Σ column widths).
///
/// The centre of gravity is `ColIntoArray<'T>` (from `Ch.Proto`),
/// surfaced here under the `Ch.Drain` import. It decodes server-sent
/// rows **directly into a caller-owned typed array**, so:
///
///   - The underlying byte buffer never resizes — references taken
///     on a different thread remain valid for the lifetime of the
///     read (this is what enables the producer/consumer SPSC drain
///     pattern: one thread fills the arrays, another reads them by
///     index against a `Volatile.Write`-published frontier).
///   - The `Row` count maps 1-to-1 to the offset inside the array
///     — `col.Rows` IS the published frontier value; no separate
///     bookkeeping.
///   - No block-by-block `CopyTo` — `Reader.ReadFull` writes
///     straight into the destination via `MemoryMarshal.AsBytes`.
///
/// The expected idiom (single-feature today; documented here so it
/// generalises later):
///
/// ```fsharp
/// open Ch.Proto
/// open Ch.Client
/// open Ch.Drain
/// open Ch.Stream
///
/// // 1. Get the total row count.
/// let mutable total = 0L
/// Stream.rows_u64 client countSql (fun n -> total <- int64 n)
/// let n = int total
///
/// // 2. Allocate typed arrays and `ColIntoArray` decoders.
/// let epochs = Array.zeroCreate<int64> n
/// let bp1    = Array.zeroCreate<float> n
/// let ap1    = Array.zeroCreate<float> n
/// let cE  = ColIntoArray(epochs)
/// let cB  = ColIntoArray(bp1)
/// let cA  = ColIntoArray(ap1)
///
/// // 3. Drain via `Stream.columns` (the escape hatch). Per-block
/// // callback publishes the frontier; no manual `CopyTo`.
/// let pub = [| 0 |]
/// Stream.columns client bookSql
///     [ cE; cB; cA ]
///     (fun _rows -> Volatile.Write(&pub.[0], cE.Rows))
/// ```
///
/// A grow-on-decode variant (`ColAccumulating<'T>`) is deferred —
/// only the pre-sized shape has a real consumer today, and the
/// pre-sized variant strictly subsumes accumulation when the total
/// row count is cheap to obtain (it almost always is, via
/// `SELECT count() FROM …` against the same predicate).
///
/// High-level tuple-of-arrays helpers (`drain.arrays_i64f64f64f64f64`
/// etc.) are deferred until the manual `ColIntoArray ×N` idiom
/// appears in more than one feature.
module Ch.Drain

/// Pre-sized drain column — re-export of `Ch.Proto.ColIntoArray<'T>`
/// under the `Ch.Drain` import. The actual type lives in `Ch.Proto`
/// because it implements `IColumnResult` (a `Ch.Proto` interface);
/// this alias exists so callers signalling "I am draining" can
/// `open Ch.Drain` instead of `open Ch.Proto`.
type ColIntoArray<'T
        when 'T : unmanaged
        and 'T : struct
        and 'T : (new : unit -> 'T)
        and 'T :> System.ValueType
        and 'T : equality
        and 'T : not null> = Ch.Proto.ColIntoArray<'T>
