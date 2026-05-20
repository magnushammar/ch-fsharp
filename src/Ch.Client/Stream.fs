namespace Ch.Stream

open System
open System.Threading
open Ch.Proto
open Ch.Client

// =============================================================================
// Delegate types for block-level typed-span callbacks.
//
// F# function values cannot hold `ReadOnlySpan<'T>` (it is a ref
// struct, banned from `FSharpFunc` parameter types by Common IL).
// The `blocks_*` helpers therefore take delegate parameters; F#
// lambdas auto-convert when the helper is a member method on a
// static class — the rule that makes `Stream.blocks_i64f64(client,
// sql, fun ts vs -> ...)` work below.
// =============================================================================

type OnBlock1<'A> =
    delegate of ReadOnlySpan<'A> -> unit
type OnBlock2<'A, 'B> =
    delegate of ReadOnlySpan<'A> * ReadOnlySpan<'B> -> unit
type OnBlock3<'A, 'B, 'C> =
    delegate of ReadOnlySpan<'A> * ReadOnlySpan<'B> * ReadOnlySpan<'C> -> unit
type OnBlock4<'A, 'B, 'C, 'D> =
    delegate of ReadOnlySpan<'A> * ReadOnlySpan<'B> *
                ReadOnlySpan<'C> * ReadOnlySpan<'D> -> unit
type OnBlock5<'A, 'B, 'C, 'D, 'E> =
    delegate of ReadOnlySpan<'A> * ReadOnlySpan<'B> *
                ReadOnlySpan<'C> * ReadOnlySpan<'D> * ReadOnlySpan<'E> -> unit
type OnBlock6<'A, 'B, 'C, 'D, 'E, 'F> =
    delegate of ReadOnlySpan<'A> * ReadOnlySpan<'B> *
                ReadOnlySpan<'C> * ReadOnlySpan<'D> *
                ReadOnlySpan<'E> * ReadOnlySpan<'F> -> unit
type OnBlock7<'A, 'B, 'C, 'D, 'E, 'F, 'G> =
    delegate of ReadOnlySpan<'A> * ReadOnlySpan<'B> *
                ReadOnlySpan<'C> * ReadOnlySpan<'D> *
                ReadOnlySpan<'E> * ReadOnlySpan<'F> * ReadOnlySpan<'G> -> unit
type OnBlock8<'A, 'B, 'C, 'D, 'E, 'F, 'G, 'H> =
    delegate of ReadOnlySpan<'A> * ReadOnlySpan<'B> *
                ReadOnlySpan<'C> * ReadOnlySpan<'D> *
                ReadOnlySpan<'E> * ReadOnlySpan<'F> *
                ReadOnlySpan<'G> * ReadOnlySpan<'H> -> unit

/// Tier 1 — streaming columnar SELECT helpers ("the fast path").
///
/// For consumers that fold each server block in place and never
/// materialise the full result. Memory footprint per call: O(max
/// block size), regardless of total row count.
///
/// Three layered shapes, all tuple-style (Client first):
///
///   1. `Stream.rows_<shape>(client, sql, action)` — per-row callback
///      with typed primitive arguments. Driver allocates the columns,
///      decodes each block in place, invokes `action` once per row.
///      Drop-in for simple-accumulator loops.
///
///   2. `Stream.blocks_<shape>(client, sql, onBlock)` — per-block
///      callback receiving `ReadOnlySpan<'T>` per column. Driver
///      allocates the columns and decodes each block in place; the
///      callback's spans are exactly `rows` long. Use this for
///      SIMD-friendly inner loops, bulk-`CopyTo` patterns, or any
///      shape where surfacing the block boundary helps.
///
///   3. `Stream.columns(client, sql, cols, onBlock)` — escape hatch
///      over `Client.Select`. Takes an `IColumnResult list` directly
///      (no record wrapping) and a per-block row-count callback.
///      For shapes outside the typed set — pathologically wide
///      reads, `Array(Float64)` mixed with primitives, or for
///      opting into Tier 2's `ColIntoArray<'T>` drain shape.
///
/// All decoded columns are valid only inside the callback that
/// observes them; subsequent blocks overwrite the in-place buffers.
/// See `ColPrimitive.AsSpan()` and `Ch.Drain.ColIntoArray` for the
/// per-column lifetime contracts.
///
/// Cancellation: the helpers pass `CancellationToken.None` through to
/// `Client.Select`. Callers that need a cancellable Select should
/// build a `SelectQuery` directly (see `Stream.columns` source) and
/// route through `client.Select(q, ct)` themselves — the helper set
/// is intentionally simple.
[<AbstractClass; Sealed>]
type Stream =

    static member inline private cr (c: #IColumnResult) : ColumnResult =
        ColumnResult.ofColumn c

    static member private runSelect (client: Client,
                                     sql: string,
                                     cols: ColumnResult list,
                                     onBlock: int -> unit) : unit =
        let q =
            { SelectQuery.defaults with
                Body    = sql
                Results = cols
                OnBlock = onBlock }
        client.Select(q, CancellationToken.None)

    // -------------------------------------------------------------------------
    // Escape hatch — caller-declared column shape.
    // -------------------------------------------------------------------------

    /// Block-level columnar primitive. Caller declares typed columns
    /// (`ColInt64()`, `ColFloat64()`, `ColArr<float>(...)`, `ColIntoArray`,
    /// …), passes them positionally, and supplies a per-block callback
    /// receiving the block's row count. Inside the callback, read each
    /// column via `.AsSpan()` (primitives) or `.Row i` (arrays /
    /// strings). Use this whenever the column shape isn't covered by
    /// a typed `blocks_*` / `rows_*` helper — wide mixed-primitive
    /// reads, `Array(Float64)` columns, or drain via `ColIntoArray`.
    static member columns(client: Client,
                          sql: string,
                          cols: IColumnResult list,
                          onBlock: int -> unit) : unit =
        let results = cols |> List.map (fun c -> { Name = ""; Column = c })
        Stream.runSelect(client, sql, results, onBlock)

    // -------------------------------------------------------------------------
    // Block-level typed helpers — `blocks_<shape>`. Custom delegate
    // params; F# lambdas auto-convert on the member-call site.
    // -------------------------------------------------------------------------

    static member blocks_i64(client: Client, sql: string,
                             onBlock: OnBlock1<int64>) : unit =
        let c0 = ColInt64()
        Stream.runSelect(client, sql, [ Stream.cr c0 ],
            fun _ -> onBlock.Invoke(c0.AsSpan()))

    static member blocks_u64(client: Client, sql: string,
                             onBlock: OnBlock1<uint64>) : unit =
        let c0 = ColUInt64()
        Stream.runSelect(client, sql, [ Stream.cr c0 ],
            fun _ -> onBlock.Invoke(c0.AsSpan()))

    static member blocks_i64f64(client: Client, sql: string,
                                onBlock: OnBlock2<int64, float>) : unit =
        let c0 = ColInt64()
        let c1 = ColFloat64()
        Stream.runSelect(client, sql, [ Stream.cr c0; Stream.cr c1 ],
            fun _ -> onBlock.Invoke(c0.AsSpan(), c1.AsSpan()))

    static member blocks_i64f64f64(client: Client, sql: string,
                                   onBlock: OnBlock3<int64, float, float>) : unit =
        let c0 = ColInt64()
        let c1 = ColFloat64()
        let c2 = ColFloat64()
        Stream.runSelect(client, sql,
            [ Stream.cr c0; Stream.cr c1; Stream.cr c2 ],
            fun _ -> onBlock.Invoke(c0.AsSpan(), c1.AsSpan(), c2.AsSpan()))

    static member blocks_i64f64f64f64(client: Client, sql: string,
                                      onBlock: OnBlock4<int64, float, float, float>) : unit =
        let c0 = ColInt64()
        let c1 = ColFloat64()
        let c2 = ColFloat64()
        let c3 = ColFloat64()
        Stream.runSelect(client, sql,
            [ Stream.cr c0; Stream.cr c1; Stream.cr c2; Stream.cr c3 ],
            fun _ -> onBlock.Invoke(c0.AsSpan(), c1.AsSpan(), c2.AsSpan(), c3.AsSpan()))

    static member blocks_i64f64f64f64f64
            (client: Client, sql: string,
             onBlock: OnBlock5<int64, float, float, float, float>) : unit =
        let c0 = ColInt64()
        let c1 = ColFloat64()
        let c2 = ColFloat64()
        let c3 = ColFloat64()
        let c4 = ColFloat64()
        Stream.runSelect(client, sql,
            [ Stream.cr c0; Stream.cr c1; Stream.cr c2; Stream.cr c3; Stream.cr c4 ],
            fun _ ->
                onBlock.Invoke(c0.AsSpan(), c1.AsSpan(), c2.AsSpan(),
                               c3.AsSpan(), c4.AsSpan()))

    static member blocks_i64f64f64f64f64f64
            (client: Client, sql: string,
             onBlock: OnBlock6<int64, float, float, float, float, float>) : unit =
        let c0 = ColInt64()
        let c1 = ColFloat64()
        let c2 = ColFloat64()
        let c3 = ColFloat64()
        let c4 = ColFloat64()
        let c5 = ColFloat64()
        Stream.runSelect(client, sql,
            [ Stream.cr c0; Stream.cr c1; Stream.cr c2;
              Stream.cr c3; Stream.cr c4; Stream.cr c5 ],
            fun _ ->
                onBlock.Invoke(c0.AsSpan(), c1.AsSpan(), c2.AsSpan(),
                               c3.AsSpan(), c4.AsSpan(), c5.AsSpan()))

    static member blocks_i64f64x7
            (client: Client, sql: string,
             onBlock: OnBlock8<int64, float, float, float, float, float, float, float>) : unit =
        let c0 = ColInt64()
        let c1 = ColFloat64()
        let c2 = ColFloat64()
        let c3 = ColFloat64()
        let c4 = ColFloat64()
        let c5 = ColFloat64()
        let c6 = ColFloat64()
        let c7 = ColFloat64()
        Stream.runSelect(client, sql,
            [ Stream.cr c0; Stream.cr c1; Stream.cr c2; Stream.cr c3;
              Stream.cr c4; Stream.cr c5; Stream.cr c6; Stream.cr c7 ],
            fun _ ->
                onBlock.Invoke(c0.AsSpan(), c1.AsSpan(), c2.AsSpan(),
                               c3.AsSpan(), c4.AsSpan(), c5.AsSpan(),
                               c6.AsSpan(), c7.AsSpan()))

    static member blocks_i64i32(client: Client, sql: string,
                                onBlock: OnBlock2<int64, int32>) : unit =
        let c0 = ColInt64()
        let c1 = ColInt32()
        Stream.runSelect(client, sql, [ Stream.cr c0; Stream.cr c1 ],
            fun _ -> onBlock.Invoke(c0.AsSpan(), c1.AsSpan()))

    static member blocks_i64f64i32
            (client: Client, sql: string,
             onBlock: OnBlock3<int64, float, int32>) : unit =
        let c0 = ColInt64()
        let c1 = ColFloat64()
        let c2 = ColInt32()
        Stream.runSelect(client, sql,
            [ Stream.cr c0; Stream.cr c1; Stream.cr c2 ],
            fun _ -> onBlock.Invoke(c0.AsSpan(), c1.AsSpan(), c2.AsSpan()))

    static member blocks_i64f64f64i32
            (client: Client, sql: string,
             onBlock: OnBlock4<int64, float, float, int32>) : unit =
        let c0 = ColInt64()
        let c1 = ColFloat64()
        let c2 = ColFloat64()
        let c3 = ColInt32()
        Stream.runSelect(client, sql,
            [ Stream.cr c0; Stream.cr c1; Stream.cr c2; Stream.cr c3 ],
            fun _ -> onBlock.Invoke(c0.AsSpan(), c1.AsSpan(), c2.AsSpan(), c3.AsSpan()))

    static member blocks_i64i64f64f64u8
            (client: Client, sql: string,
             onBlock: OnBlock5<int64, int64, float, float, uint8>) : unit =
        let c0 = ColInt64()
        let c1 = ColInt64()
        let c2 = ColFloat64()
        let c3 = ColFloat64()
        let c4 = ColUInt8()
        Stream.runSelect(client, sql,
            [ Stream.cr c0; Stream.cr c1; Stream.cr c2; Stream.cr c3; Stream.cr c4 ],
            fun _ ->
                onBlock.Invoke(c0.AsSpan(), c1.AsSpan(), c2.AsSpan(),
                               c3.AsSpan(), c4.AsSpan()))

    // -------------------------------------------------------------------------
    // Per-row typed helpers — `rows_<shape>`. Regular F# function
    // params (no Span), no delegate ceremony needed.
    // -------------------------------------------------------------------------

    static member rows_i64(client: Client, sql: string,
                           action: int64 -> unit) : unit =
        let c0 = ColInt64()
        Stream.runSelect(client, sql, [ Stream.cr c0 ], fun rows ->
            let s0 = c0.AsSpan()
            for i in 0 .. rows - 1 do action s0.[i])

    static member rows_u64(client: Client, sql: string,
                           action: uint64 -> unit) : unit =
        let c0 = ColUInt64()
        Stream.runSelect(client, sql, [ Stream.cr c0 ], fun rows ->
            let s0 = c0.AsSpan()
            for i in 0 .. rows - 1 do action s0.[i])

    static member rows_i64f64(client: Client, sql: string,
                              action: int64 -> float -> unit) : unit =
        let c0 = ColInt64()
        let c1 = ColFloat64()
        Stream.runSelect(client, sql, [ Stream.cr c0; Stream.cr c1 ], fun rows ->
            let s0 = c0.AsSpan()
            let s1 = c1.AsSpan()
            for i in 0 .. rows - 1 do action s0.[i] s1.[i])

    static member rows_i64f64f64(client: Client, sql: string,
                                 action: int64 -> float -> float -> unit) : unit =
        let c0 = ColInt64()
        let c1 = ColFloat64()
        let c2 = ColFloat64()
        Stream.runSelect(client, sql,
            [ Stream.cr c0; Stream.cr c1; Stream.cr c2 ], fun rows ->
            let s0 = c0.AsSpan()
            let s1 = c1.AsSpan()
            let s2 = c2.AsSpan()
            for i in 0 .. rows - 1 do action s0.[i] s1.[i] s2.[i])

    static member rows_i64f64f64f64
            (client: Client, sql: string,
             action: int64 -> float -> float -> float -> unit) : unit =
        let c0 = ColInt64()
        let c1 = ColFloat64()
        let c2 = ColFloat64()
        let c3 = ColFloat64()
        Stream.runSelect(client, sql,
            [ Stream.cr c0; Stream.cr c1; Stream.cr c2; Stream.cr c3 ], fun rows ->
            let s0 = c0.AsSpan()
            let s1 = c1.AsSpan()
            let s2 = c2.AsSpan()
            let s3 = c3.AsSpan()
            for i in 0 .. rows - 1 do action s0.[i] s1.[i] s2.[i] s3.[i])

    static member rows_i64f64f64f64f64
            (client: Client, sql: string,
             action: int64 -> float -> float -> float -> float -> unit) : unit =
        let c0 = ColInt64()
        let c1 = ColFloat64()
        let c2 = ColFloat64()
        let c3 = ColFloat64()
        let c4 = ColFloat64()
        Stream.runSelect(client, sql,
            [ Stream.cr c0; Stream.cr c1; Stream.cr c2; Stream.cr c3; Stream.cr c4 ],
            fun rows ->
                let s0 = c0.AsSpan()
                let s1 = c1.AsSpan()
                let s2 = c2.AsSpan()
                let s3 = c3.AsSpan()
                let s4 = c4.AsSpan()
                for i in 0 .. rows - 1 do action s0.[i] s1.[i] s2.[i] s3.[i] s4.[i])

    static member rows_i64f64f64f64f64f64
            (client: Client, sql: string,
             action: int64 -> float -> float -> float -> float -> float -> unit) : unit =
        let c0 = ColInt64()
        let c1 = ColFloat64()
        let c2 = ColFloat64()
        let c3 = ColFloat64()
        let c4 = ColFloat64()
        let c5 = ColFloat64()
        Stream.runSelect(client, sql,
            [ Stream.cr c0; Stream.cr c1; Stream.cr c2;
              Stream.cr c3; Stream.cr c4; Stream.cr c5 ],
            fun rows ->
                let s0 = c0.AsSpan()
                let s1 = c1.AsSpan()
                let s2 = c2.AsSpan()
                let s3 = c3.AsSpan()
                let s4 = c4.AsSpan()
                let s5 = c5.AsSpan()
                for i in 0 .. rows - 1 do
                    action s0.[i] s1.[i] s2.[i] s3.[i] s4.[i] s5.[i])

    static member rows_i64f64x7
            (client: Client, sql: string,
             action: int64 -> float -> float -> float -> float -> float -> float -> float -> unit)
            : unit =
        let c0 = ColInt64()
        let c1 = ColFloat64()
        let c2 = ColFloat64()
        let c3 = ColFloat64()
        let c4 = ColFloat64()
        let c5 = ColFloat64()
        let c6 = ColFloat64()
        let c7 = ColFloat64()
        Stream.runSelect(client, sql,
            [ Stream.cr c0; Stream.cr c1; Stream.cr c2; Stream.cr c3;
              Stream.cr c4; Stream.cr c5; Stream.cr c6; Stream.cr c7 ],
            fun rows ->
                let s0 = c0.AsSpan()
                let s1 = c1.AsSpan()
                let s2 = c2.AsSpan()
                let s3 = c3.AsSpan()
                let s4 = c4.AsSpan()
                let s5 = c5.AsSpan()
                let s6 = c6.AsSpan()
                let s7 = c7.AsSpan()
                for i in 0 .. rows - 1 do
                    action s0.[i] s1.[i] s2.[i] s3.[i] s4.[i] s5.[i] s6.[i] s7.[i])

    static member rows_i64i32(client: Client, sql: string,
                              action: int64 -> int32 -> unit) : unit =
        let c0 = ColInt64()
        let c1 = ColInt32()
        Stream.runSelect(client, sql, [ Stream.cr c0; Stream.cr c1 ], fun rows ->
            let s0 = c0.AsSpan()
            let s1 = c1.AsSpan()
            for i in 0 .. rows - 1 do action s0.[i] s1.[i])

    static member rows_i64f64i32
            (client: Client, sql: string,
             action: int64 -> float -> int32 -> unit) : unit =
        let c0 = ColInt64()
        let c1 = ColFloat64()
        let c2 = ColInt32()
        Stream.runSelect(client, sql,
            [ Stream.cr c0; Stream.cr c1; Stream.cr c2 ], fun rows ->
            let s0 = c0.AsSpan()
            let s1 = c1.AsSpan()
            let s2 = c2.AsSpan()
            for i in 0 .. rows - 1 do action s0.[i] s1.[i] s2.[i])

    static member rows_i64f64f64i32
            (client: Client, sql: string,
             action: int64 -> float -> float -> int32 -> unit) : unit =
        let c0 = ColInt64()
        let c1 = ColFloat64()
        let c2 = ColFloat64()
        let c3 = ColInt32()
        Stream.runSelect(client, sql,
            [ Stream.cr c0; Stream.cr c1; Stream.cr c2; Stream.cr c3 ], fun rows ->
            let s0 = c0.AsSpan()
            let s1 = c1.AsSpan()
            let s2 = c2.AsSpan()
            let s3 = c3.AsSpan()
            for i in 0 .. rows - 1 do action s0.[i] s1.[i] s2.[i] s3.[i])

    static member rows_i64i64f64f64u8
            (client: Client, sql: string,
             action: int64 -> int64 -> float -> float -> uint8 -> unit) : unit =
        let c0 = ColInt64()
        let c1 = ColInt64()
        let c2 = ColFloat64()
        let c3 = ColFloat64()
        let c4 = ColUInt8()
        Stream.runSelect(client, sql,
            [ Stream.cr c0; Stream.cr c1; Stream.cr c2; Stream.cr c3; Stream.cr c4 ],
            fun rows ->
                let s0 = c0.AsSpan()
                let s1 = c1.AsSpan()
                let s2 = c2.AsSpan()
                let s3 = c3.AsSpan()
                let s4 = c4.AsSpan()
                for i in 0 .. rows - 1 do action s0.[i] s1.[i] s2.[i] s3.[i] s4.[i])

    /// Int64 + Nullable(Float64), null delivered as `nan`.
    ///
    /// Sole callsite shape: `SELECT bar, if(cond, x, NULL) FROM ...` —
    /// ClickHouse types the second column as `Nullable(Float64)`, and
    /// a NaN sentinel is the simplest in-band null representation for
    /// downstream float math.
    static member rows_i64f64n
            (client: Client, sql: string, action: int64 -> float -> unit) : unit =
        let c0 = ColInt64()
        let c1 = ColNullable<float>(ColFloat64() :> IColumnOf<float>)
        Stream.runSelect(client, sql,
            [ Stream.cr c0; Stream.cr c1 ], fun rows ->
                let s0 = c0.AsSpan()
                for i in 0 .. rows - 1 do
                    let v = if c1.IsNull i then nan else c1.Inner.Row i
                    action s0.[i] v)

    // -------------------------------------------------------------------------
    // Array(Float64) shape helpers — `rows_*` only.
    //
    // `ColArr<'T>.Row i` materialises a fresh `'T[]` per row, so a
    // "block-level span of arrays" doesn't have meaningful semantics
    // (no contiguous typed view exists). These shapes ship per-row
    // only; callers needing block-level control over array columns
    // drop to `Stream.columns` and declare `ColArr<float>(ColFloat64())`
    // directly.
    // -------------------------------------------------------------------------

    static member rows_i64a64a64
            (client: Client, sql: string,
             action: int64 -> float[] -> float[] -> unit) : unit =
        let c0 = ColInt64()
        let c1 = ColArr<float>(ColFloat64() :> IColumnOf<float>)
        let c2 = ColArr<float>(ColFloat64() :> IColumnOf<float>)
        Stream.runSelect(client, sql,
            [ Stream.cr c0; Stream.cr c1; Stream.cr c2 ], fun rows ->
                let s0 = c0.AsSpan()
                for i in 0 .. rows - 1 do action s0.[i] (c1.Row i) (c2.Row i))

    static member rows_i64i64a64a64
            (client: Client, sql: string,
             action: int64 -> int64 -> float[] -> float[] -> unit) : unit =
        let c0 = ColInt64()
        let c1 = ColInt64()
        let c2 = ColArr<float>(ColFloat64() :> IColumnOf<float>)
        let c3 = ColArr<float>(ColFloat64() :> IColumnOf<float>)
        Stream.runSelect(client, sql,
            [ Stream.cr c0; Stream.cr c1; Stream.cr c2; Stream.cr c3 ], fun rows ->
                let s0 = c0.AsSpan()
                let s1 = c1.AsSpan()
                for i in 0 .. rows - 1 do action s0.[i] s1.[i] (c2.Row i) (c3.Row i))

    static member rows_i64i64a64a64a64a64
            (client: Client, sql: string,
             action: int64 -> int64 -> float[] -> float[] -> float[] -> float[] -> unit) : unit =
        let c0 = ColInt64()
        let c1 = ColInt64()
        let c2 = ColArr<float>(ColFloat64() :> IColumnOf<float>)
        let c3 = ColArr<float>(ColFloat64() :> IColumnOf<float>)
        let c4 = ColArr<float>(ColFloat64() :> IColumnOf<float>)
        let c5 = ColArr<float>(ColFloat64() :> IColumnOf<float>)
        Stream.runSelect(client, sql,
            [ Stream.cr c0; Stream.cr c1; Stream.cr c2;
              Stream.cr c3; Stream.cr c4; Stream.cr c5 ],
            fun rows ->
                let s0 = c0.AsSpan()
                let s1 = c1.AsSpan()
                for i in 0 .. rows - 1 do
                    action s0.[i] s1.[i] (c2.Row i) (c3.Row i) (c4.Row i) (c5.Row i))
