module Ch.Proto.Tests.RetryTests

open System
open System.IO
open System.Net.Sockets
open Expecto
open Ch.Proto
open Ch.Stream

/// A retryable-or-not F# exception, to confirm the classifier rejects
/// exception shapes outside its explicit match list.
exception private TestExn of string

/// Policies that pin the classifier so the gate / attempt-count logic
/// can be exercised in isolation from `isRetryableNetwork`.
let private alwaysRetry = { RetryPolicy.defaults with IsRetryable = fun _ -> true }
let private neverRetry  = { RetryPolicy.defaults with IsRetryable = fun _ -> false }

[<Tests>]
let tests = testList "Retry" [

    // -------------------------------------------------------------------
    // RetryPolicy.isRetryableNetwork — pure exception classifier.
    // -------------------------------------------------------------------
    testList "isRetryableNetwork" [
        testCase "IOException is retryable" <| fun _ ->
            Expect.isTrue
                (RetryPolicy.isRetryableNetwork (IOException()))
                "IOException — transient I/O failure"

        testCase "IOException subclass (EndOfStreamException) is retryable" <| fun _ ->
            Expect.isTrue
                (RetryPolicy.isRetryableNetwork (EndOfStreamException()))
                "EndOfStreamException : IOException — caught by the base-type match"

        testCase "SocketException is retryable" <| fun _ ->
            Expect.isTrue
                (RetryPolicy.isRetryableNetwork (SocketException()))
                "SocketException — transient network failure"

        testCase "TimeoutException is retryable" <| fun _ ->
            Expect.isTrue
                (RetryPolicy.isRetryableNetwork (TimeoutException()))
                "TimeoutException — transient"

        testCase "ObjectDisposedException is retryable" <| fun _ ->
            Expect.isTrue
                (RetryPolicy.isRetryableNetwork (ObjectDisposedException("socket")))
                "ObjectDisposedException — socket force-closed mid-read"

        testCase "UnexpectedEndOfStreamException is retryable" <| fun _ ->
            Expect.isTrue
                (RetryPolicy.isRetryableNetwork
                    (UnexpectedEndOfStreamException "server drop mid-block"))
                "driver's own end-of-stream exception — server-side drop"

        testCase "InvalidDataException is NOT retryable" <| fun _ ->
            Expect.isFalse
                (RetryPolicy.isRetryableNetwork (InvalidDataException("bad query")))
                "InvalidDataException carries server-side query errors — never retry"

        testCase "plain Exception is NOT retryable" <| fun _ ->
            Expect.isFalse
                (RetryPolicy.isRetryableNetwork (Exception("boom")))
                "generic Exception — not classified transient"

        testCase "F# exception is NOT retryable" <| fun _ ->
            Expect.isFalse
                (RetryPolicy.isRetryableNetwork (TestExn "x"))
                "an exception outside the match list defaults to non-retryable"
    ]

    // -------------------------------------------------------------------
    // RetryPolicy.defaults — record field values.
    // -------------------------------------------------------------------
    testList "RetryPolicy.defaults" [
        testCase "5 attempts, 1000 ms base backoff" <| fun _ ->
            Expect.equal RetryPolicy.defaults.MaxAttempts 5 "MaxAttempts"
            Expect.equal RetryPolicy.defaults.BaseBackoffMs 1000 "BaseBackoffMs"

        testCase "default classifier delegates to isRetryableNetwork" <| fun _ ->
            Expect.isTrue
                (RetryPolicy.defaults.IsRetryable (IOException()))
                "true case routes through isRetryableNetwork"
            Expect.isFalse
                (RetryPolicy.defaults.IsRetryable (Exception()))
                "false case routes through isRetryableNetwork"
    ]

    // -------------------------------------------------------------------
    // RetryCore.run — the retry control loop (server-free; the attempt
    // thunk and the sleep are both injected).
    // -------------------------------------------------------------------
    testList "RetryCore.run" [
        testCase "success on first attempt — no retry, no sleep" <| fun _ ->
            let gate = ref false
            let sleeps = ResizeArray<int>()
            let mutable runs = 0
            RetryCore.run RetryPolicy.defaults gate sleeps.Add (fun () ->
                runs <- runs + 1)
            Expect.equal runs 1 "attempt ran exactly once"
            Expect.equal sleeps.Count 0 "no backoff sleep on a clean run"

        testCase "retryable failure then success — retries and recovers" <| fun _ ->
            let gate = ref false
            let sleeps = ResizeArray<int>()
            let mutable runs = 0
            RetryCore.run alwaysRetry gate sleeps.Add (fun () ->
                runs <- runs + 1
                if runs < 3 then raise (IOException()))
            Expect.equal runs 3 "failed twice, succeeded on the third attempt"
            Expect.sequenceEqual sleeps [ 1000; 2000 ]
                "backoff doubled before each of the two retries"

        testCase "exhausting MaxAttempts propagates the final exception" <| fun _ ->
            let gate = ref false
            let sleeps = ResizeArray<int>()
            let mutable runs = 0
            Expect.throwsT<IOException>
                (fun () ->
                    RetryCore.run alwaysRetry gate sleeps.Add (fun () ->
                        runs <- runs + 1
                        raise (IOException())))
                "all attempts fail — the last exception escapes"
            Expect.equal runs 5 "ran MaxAttempts times (1 initial + 4 retries)"
            Expect.sequenceEqual sleeps [ 1000; 2000; 4000; 8000 ]
                "exponential backoff 1-2-4-8 s before the four retries"

        testCase "non-retryable exception propagates immediately" <| fun _ ->
            let gate = ref false
            let sleeps = ResizeArray<int>()
            let mutable runs = 0
            Expect.throwsT<IOException>
                (fun () ->
                    RetryCore.run neverRetry gate sleeps.Add (fun () ->
                        runs <- runs + 1
                        raise (IOException())))
                "classifier says no — the first failure escapes"
            Expect.equal runs 1 "no retry attempted"
            Expect.equal sleeps.Count 0 "no backoff"

        testCase "exception after the gate flips propagates (no double-count)" <| fun _ ->
            // The load-bearing correctness property: once the attempt has
            // done stateful work (gate := true), a failure must NOT be
            // retried — re-running would double-count the accumulators.
            let gate = ref false
            let sleeps = ResizeArray<int>()
            let mutable runs = 0
            Expect.throwsT<IOException>
                (fun () ->
                    RetryCore.run alwaysRetry gate sleeps.Add (fun () ->
                        runs <- runs + 1
                        gate.Value <- true            // stateful work started
                        raise (IOException())))
                "retryable failure, but the gate is set — must escape"
            Expect.equal runs 1 "gate blocks the retry despite a retryable classification"
            Expect.equal sleeps.Count 0 "no backoff"

        testCase "MaxAttempts = 1 means no retry" <| fun _ ->
            let gate = ref false
            let sleeps = ResizeArray<int>()
            let mutable runs = 0
            let policy = { alwaysRetry with MaxAttempts = 1 }
            Expect.throwsT<IOException>
                (fun () ->
                    RetryCore.run policy gate sleeps.Add (fun () ->
                        runs <- runs + 1
                        raise (IOException())))
                "MaxAttempts=1 — single shot, the failure escapes"
            Expect.equal runs 1 "exactly one attempt"
            Expect.equal sleeps.Count 0 "no backoff"
    ]
]
