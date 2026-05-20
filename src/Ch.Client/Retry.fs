namespace Ch.Stream

open System
open System.IO
open System.Net.Sockets
open System.Threading
open Ch.Proto
open Ch.Client

// =============================================================================
// Retry policy — orthogonal to connection pooling.
//
// The driver does no pooling: `Client.Connect` is cheap (~0.5 ms on
// localhost) and per-query connections sidestep stale-socket and
// session-state-bleed concerns. Retry is a separate concern — a
// helper that opens a fresh Client per attempt and re-runs the
// action on classified-transient exceptions.
//
// The kernel-state safety gate (`receivedAny`) is what makes retry
// *correct*: once the action's OnBlock has fired, the caller's
// accumulators have been mutated; re-running the SELECT would
// double-count from the top. The gate is a `bool ref` the action
// flips on first stateful work — typically the first line of its
// OnBlock callback. Retry STOPS once the gate is set.
//
// All helpers in `Retry.*` mirror `Stream.*` shape-for-shape and
// flip the gate internally on behalf of the caller. Use
// `Retry.run` (the escape hatch) when you compose with
// `Stream.columns` or anything outside the typed shape set.
// =============================================================================

/// Bounded-retry-with-exponential-backoff policy.
///
/// `IsRetryable` classifies an exception as retry-eligible: network
/// failures and timeouts yes; server-side query errors no. The
/// default `RetryPolicy.isRetryableNetwork` catches the typical
/// transient set.
type RetryPolicy = {
    /// Total attempts including the first. `1` = no retry; `5` =
    /// initial + 4 retries.
    MaxAttempts : int
    /// Sleep before retry `n` (zero-indexed) is
    /// `BaseBackoffMs * 2^n`. With the default 1000 ms base:
    /// 1 s, 2 s, 4 s, 8 s before retries 1..4.
    BaseBackoffMs : int
    /// Classifier — return `true` iff this exception should be
    /// retried (assuming the kernel-state gate is still clear).
    IsRetryable : exn -> bool
}

[<RequireQualifiedAccess>]
module RetryPolicy =

    /// Default classifier for transient network / I/O failures.
    /// `true` for `IOException`, `SocketException`, `TimeoutException`,
    /// `ObjectDisposedException` (raised when a cancellation token
    /// fires and force-closes the socket mid-read), and the driver's
    /// own `UnexpectedEndOfStreamException` (typically a server-side
    /// drop mid-block). Returns `false` for everything else —
    /// `InvalidDataException` (server-side query errors), F#
    /// exceptions, generic `Exception`. Callers wanting a wider or
    /// narrower set override `IsRetryable` on their policy.
    let isRetryableNetwork (ex: exn) : bool =
        match ex with
        | :? IOException                  -> true
        | :? SocketException              -> true
        | :? TimeoutException             -> true
        | :? ObjectDisposedException      -> true
        | :? UnexpectedEndOfStreamException -> true
        | _ -> false

    /// 5 attempts (1 initial + 4 retries), 1-2-4-8 s exponential
    /// backoff, `isRetryableNetwork` classifier.
    let defaults : RetryPolicy = {
        MaxAttempts   = 5
        BaseBackoffMs = 1000
        IsRetryable   = isRetryableNetwork
    }

/// Pure retry control loop, factored out of `Retry.loop` so the
/// retry decision logic — classifier ∧ gate ∧ attempt-count, plus
/// the `BaseBackoffMs · 2ⁿ` backoff schedule — is unit-testable
/// without a live server. `Retry.loop` is the sole production caller
/// and supplies the real `Client.Connect` + `Thread.Sleep`; tests
/// supply a throwing thunk and a recording `sleep`.
[<RequireQualifiedAccess>]
module internal RetryCore =

    /// Run `attempt` under `policy`'s retry envelope.
    ///
    /// Returns normally as soon as `attempt` completes without
    /// throwing. Re-runs `attempt` when it throws an exception that
    /// is `policy.IsRetryable`, the `gate` is still clear, and fewer
    /// than `MaxAttempts` runs have happened — calling `sleep` with
    /// `BaseBackoffMs · 2ⁿ` before retry `n` (zero-indexed). Any
    /// other thrown exception — non-retryable, gate already set, or
    /// attempts exhausted — propagates unchanged.
    ///
    /// `gate` is the kernel-state safety latch: `attempt` flips it
    /// `true` on its first stateful work, after which a thrown
    /// exception always propagates (re-running would double-count
    /// the caller's accumulators).
    let run (policy: RetryPolicy)
            (gate: bool ref)
            (sleep: int -> unit)
            (attempt: unit -> unit) : unit =
        let mutable n = 0
        let mutable success = false
        while not success do
            try
                attempt ()
                success <- true
            with
            | ex when policy.IsRetryable ex
                      && not gate.Value
                      && n < policy.MaxAttempts - 1 ->
                sleep (policy.BaseBackoffMs * (1 <<< n))
                n <- n + 1

/// Bounded-retry helpers that mirror `Stream.*` shape-for-shape.
///
/// Each helper opens a fresh `Client` per attempt via
/// `Client.Connect(opts, CancellationToken.None)`, invokes the
/// matching `Stream` shape, and retries on classified-transient
/// exceptions thrown before the action's first stateful work.
///
/// The `gate` is owned by the helper — callers don't see it
/// for the typed shapes. For the escape-hatch `Stream.columns`
/// path use `Retry.run`, which exposes a `bool ref` the caller
/// flips manually inside the per-block callback.
[<AbstractClass; Sealed>]
type Retry =

    static member private loop
            (opts: ChOptions, policy: RetryPolicy,
             action: Client -> bool ref -> unit) : unit =
        let gate = ref false
        RetryCore.run policy gate (fun (ms: int) -> Thread.Sleep ms) (fun () ->
            use client = Client.Connect(opts, CancellationToken.None)
            action client gate)

    // -------------------------------------------------------------------------
    // Escape hatch — generic.
    // -------------------------------------------------------------------------

    /// Open a fresh `Client` and run `action` against it with
    /// `policy`'s retry envelope. `action` receives the Client and
    /// a `bool ref` it MUST flip to `true` the first time stateful
    /// work happens (typically the first line of an OnBlock body).
    /// After the gate flips, no further retry is attempted — the
    /// exception propagates.
    ///
    /// Use this for `Stream.columns` callers and any shape outside
    /// the typed set.
    static member run(opts: ChOptions, policy: RetryPolicy,
                      action: Client -> bool ref -> unit) : unit =
        Retry.loop(opts, policy, action)

    // -------------------------------------------------------------------------
    // Per-shape wrappers — `rows_*`. The driver flips the gate at
    // the top of every per-row dispatch, freeing the caller's
    // action of any retry awareness.
    // -------------------------------------------------------------------------

    static member rows_i64(opts: ChOptions, policy: RetryPolicy,
                           sql: string, action: int64 -> unit) : unit =
        Retry.loop(opts, policy, fun client gate ->
            Stream.rows_i64(client, sql, fun v ->
                gate.Value <- true
                action v))

    static member rows_u64(opts: ChOptions, policy: RetryPolicy,
                           sql: string, action: uint64 -> unit) : unit =
        Retry.loop(opts, policy, fun client gate ->
            Stream.rows_u64(client, sql, fun v ->
                gate.Value <- true
                action v))

    static member rows_i64f64(opts: ChOptions, policy: RetryPolicy,
                              sql: string, action: int64 -> float -> unit) : unit =
        Retry.loop(opts, policy, fun client gate ->
            Stream.rows_i64f64(client, sql, fun ts v ->
                gate.Value <- true
                action ts v))

    static member rows_i64f64f64(opts: ChOptions, policy: RetryPolicy,
                                 sql: string,
                                 action: int64 -> float -> float -> unit) : unit =
        Retry.loop(opts, policy, fun client gate ->
            Stream.rows_i64f64f64(client, sql, fun ts v1 v2 ->
                gate.Value <- true
                action ts v1 v2))

    static member rows_i64f64f64f64(opts: ChOptions, policy: RetryPolicy,
                                    sql: string,
                                    action: int64 -> float -> float -> float -> unit) : unit =
        Retry.loop(opts, policy, fun client gate ->
            Stream.rows_i64f64f64f64(client, sql, fun ts v1 v2 v3 ->
                gate.Value <- true
                action ts v1 v2 v3))

    static member rows_i64f64f64f64f64
            (opts: ChOptions, policy: RetryPolicy, sql: string,
             action: int64 -> float -> float -> float -> float -> unit) : unit =
        Retry.loop(opts, policy, fun client gate ->
            Stream.rows_i64f64f64f64f64(client, sql, fun ts v1 v2 v3 v4 ->
                gate.Value <- true
                action ts v1 v2 v3 v4))

    static member rows_i64f64f64f64f64f64
            (opts: ChOptions, policy: RetryPolicy, sql: string,
             action: int64 -> float -> float -> float -> float -> float -> unit) : unit =
        Retry.loop(opts, policy, fun client gate ->
            Stream.rows_i64f64f64f64f64f64(client, sql, fun ts v1 v2 v3 v4 v5 ->
                gate.Value <- true
                action ts v1 v2 v3 v4 v5))

    static member rows_i64f64x7
            (opts: ChOptions, policy: RetryPolicy, sql: string,
             action: int64 -> float -> float -> float -> float -> float -> float -> float -> unit)
            : unit =
        Retry.loop(opts, policy, fun client gate ->
            Stream.rows_i64f64x7(client, sql, fun ts v1 v2 v3 v4 v5 v6 v7 ->
                gate.Value <- true
                action ts v1 v2 v3 v4 v5 v6 v7))

    static member rows_i64i32(opts: ChOptions, policy: RetryPolicy,
                              sql: string,
                              action: int64 -> int32 -> unit) : unit =
        Retry.loop(opts, policy, fun client gate ->
            Stream.rows_i64i32(client, sql, fun ts v ->
                gate.Value <- true
                action ts v))

    static member rows_i64f64i32
            (opts: ChOptions, policy: RetryPolicy, sql: string,
             action: int64 -> float -> int32 -> unit) : unit =
        Retry.loop(opts, policy, fun client gate ->
            Stream.rows_i64f64i32(client, sql, fun ts v1 v2 ->
                gate.Value <- true
                action ts v1 v2))

    static member rows_i64f64f64i32
            (opts: ChOptions, policy: RetryPolicy, sql: string,
             action: int64 -> float -> float -> int32 -> unit) : unit =
        Retry.loop(opts, policy, fun client gate ->
            Stream.rows_i64f64f64i32(client, sql, fun ts v1 v2 v3 ->
                gate.Value <- true
                action ts v1 v2 v3))

    static member rows_i64i64f64f64u8
            (opts: ChOptions, policy: RetryPolicy, sql: string,
             action: int64 -> int64 -> float -> float -> uint8 -> unit) : unit =
        Retry.loop(opts, policy, fun client gate ->
            Stream.rows_i64i64f64f64u8(client, sql, fun a b c d e ->
                gate.Value <- true
                action a b c d e))

    static member rows_i64f64n
            (opts: ChOptions, policy: RetryPolicy, sql: string,
             action: int64 -> float -> unit) : unit =
        Retry.loop(opts, policy, fun client gate ->
            Stream.rows_i64f64n(client, sql, fun ts v ->
                gate.Value <- true
                action ts v))

    static member rows_i64a64a64
            (opts: ChOptions, policy: RetryPolicy, sql: string,
             action: int64 -> float[] -> float[] -> unit) : unit =
        Retry.loop(opts, policy, fun client gate ->
            Stream.rows_i64a64a64(client, sql, fun ts xs ys ->
                gate.Value <- true
                action ts xs ys))

    static member rows_i64i64a64a64
            (opts: ChOptions, policy: RetryPolicy, sql: string,
             action: int64 -> int64 -> float[] -> float[] -> unit) : unit =
        Retry.loop(opts, policy, fun client gate ->
            Stream.rows_i64i64a64a64(client, sql, fun a b xs ys ->
                gate.Value <- true
                action a b xs ys))

    static member rows_i64i64a64a64a64a64
            (opts: ChOptions, policy: RetryPolicy, sql: string,
             action: int64 -> int64 -> float[] -> float[] -> float[] -> float[] -> unit) : unit =
        Retry.loop(opts, policy, fun client gate ->
            Stream.rows_i64i64a64a64a64a64(client, sql, fun a b xs ys zs ws ->
                gate.Value <- true
                action a b xs ys zs ws))

    // -------------------------------------------------------------------------
    // Per-shape wrappers — `blocks_*`. The driver flips the gate at
    // the top of every block dispatch via a wrapping delegate.
    // -------------------------------------------------------------------------

    static member blocks_i64(opts: ChOptions, policy: RetryPolicy,
                             sql: string, onBlock: OnBlock1<int64>) : unit =
        Retry.loop(opts, policy, fun client gate ->
            Stream.blocks_i64(client, sql,
                OnBlock1(fun s -> gate.Value <- true; onBlock.Invoke(s))))

    static member blocks_u64(opts: ChOptions, policy: RetryPolicy,
                             sql: string, onBlock: OnBlock1<uint64>) : unit =
        Retry.loop(opts, policy, fun client gate ->
            Stream.blocks_u64(client, sql,
                OnBlock1(fun s -> gate.Value <- true; onBlock.Invoke(s))))

    static member blocks_i64f64(opts: ChOptions, policy: RetryPolicy,
                                sql: string,
                                onBlock: OnBlock2<int64, float>) : unit =
        Retry.loop(opts, policy, fun client gate ->
            Stream.blocks_i64f64(client, sql,
                OnBlock2(fun a b -> gate.Value <- true; onBlock.Invoke(a, b))))

    static member blocks_i64f64f64
            (opts: ChOptions, policy: RetryPolicy, sql: string,
             onBlock: OnBlock3<int64, float, float>) : unit =
        Retry.loop(opts, policy, fun client gate ->
            Stream.blocks_i64f64f64(client, sql,
                OnBlock3(fun a b c -> gate.Value <- true; onBlock.Invoke(a, b, c))))

    static member blocks_i64f64f64f64
            (opts: ChOptions, policy: RetryPolicy, sql: string,
             onBlock: OnBlock4<int64, float, float, float>) : unit =
        Retry.loop(opts, policy, fun client gate ->
            Stream.blocks_i64f64f64f64(client, sql,
                OnBlock4(fun a b c d -> gate.Value <- true; onBlock.Invoke(a, b, c, d))))

    static member blocks_i64f64f64f64f64
            (opts: ChOptions, policy: RetryPolicy, sql: string,
             onBlock: OnBlock5<int64, float, float, float, float>) : unit =
        Retry.loop(opts, policy, fun client gate ->
            Stream.blocks_i64f64f64f64f64(client, sql,
                OnBlock5(fun a b c d e -> gate.Value <- true; onBlock.Invoke(a, b, c, d, e))))

    static member blocks_i64f64f64f64f64f64
            (opts: ChOptions, policy: RetryPolicy, sql: string,
             onBlock: OnBlock6<int64, float, float, float, float, float>) : unit =
        Retry.loop(opts, policy, fun client gate ->
            Stream.blocks_i64f64f64f64f64f64(client, sql,
                OnBlock6(fun a b c d e f ->
                    gate.Value <- true
                    onBlock.Invoke(a, b, c, d, e, f))))

    static member blocks_i64f64x7
            (opts: ChOptions, policy: RetryPolicy, sql: string,
             onBlock: OnBlock8<int64, float, float, float, float, float, float, float>) : unit =
        Retry.loop(opts, policy, fun client gate ->
            Stream.blocks_i64f64x7(client, sql,
                OnBlock8(fun a b c d e f g h ->
                    gate.Value <- true
                    onBlock.Invoke(a, b, c, d, e, f, g, h))))

    static member blocks_i64i32(opts: ChOptions, policy: RetryPolicy,
                                sql: string,
                                onBlock: OnBlock2<int64, int32>) : unit =
        Retry.loop(opts, policy, fun client gate ->
            Stream.blocks_i64i32(client, sql,
                OnBlock2(fun a b -> gate.Value <- true; onBlock.Invoke(a, b))))

    static member blocks_i64f64i32
            (opts: ChOptions, policy: RetryPolicy, sql: string,
             onBlock: OnBlock3<int64, float, int32>) : unit =
        Retry.loop(opts, policy, fun client gate ->
            Stream.blocks_i64f64i32(client, sql,
                OnBlock3(fun a b c -> gate.Value <- true; onBlock.Invoke(a, b, c))))

    static member blocks_i64f64f64i32
            (opts: ChOptions, policy: RetryPolicy, sql: string,
             onBlock: OnBlock4<int64, float, float, int32>) : unit =
        Retry.loop(opts, policy, fun client gate ->
            Stream.blocks_i64f64f64i32(client, sql,
                OnBlock4(fun a b c d -> gate.Value <- true; onBlock.Invoke(a, b, c, d))))

    static member blocks_i64i64f64f64u8
            (opts: ChOptions, policy: RetryPolicy, sql: string,
             onBlock: OnBlock5<int64, int64, float, float, uint8>) : unit =
        Retry.loop(opts, policy, fun client gate ->
            Stream.blocks_i64i64f64f64u8(client, sql,
                OnBlock5(fun a b c d e ->
                    gate.Value <- true
                    onBlock.Invoke(a, b, c, d, e))))
