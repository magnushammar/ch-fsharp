module Bench.Numbers.Program

// Minimal port of ch-bench's `ch-bench-numbers`. Hard-coded query, no flags.
//
//   SELECT number FROM system.numbers_mt LIMIT 500_000_000
//
// Reads CLICKHOUSE_PASSWORD from env. Verifies row count + sum so the JIT
// can't elide the decode. Prints one OK line on stderr and exits 0.

open System
open System.Diagnostics
open Ch.Proto
open Ch.Client

[<EntryPoint>]
let main _ =
    let rows = 500_000_000L
    let password =
        match Environment.GetEnvironmentVariable "CLICKHOUSE_PASSWORD" with
        | null -> "" | s -> s
    // max_block_size is swept during perf testing — CH_BLOCK_SIZE overrides
    // the 65536 default. Bigger blocks = fewer per-block headers, but the
    // column buffer must stay <= the 2 MB L2 or the sum goes DRAM-bound.
    let blockSize =
        match Environment.GetEnvironmentVariable "CH_BLOCK_SIZE" with
        | null | "" -> 65536
        | s -> int s

    let opts =
        { ChOptions.defaults with
            Address    = "127.0.0.1:9000"
            User       = "default"
            Database   = "default"
            Password   = password
            ClientName = "ch-fsharp.bench-numbers" }

    use cts = new System.Threading.CancellationTokenSource()
    use client = Client.ConnectAsync(opts, cts.Token).GetAwaiter().GetResult()

    let col = ColUInt64()
    let mutable totalSum  = 0UL
    let mutable totalRows = 0L
    // Diagnostic: accumulate ticks spent in the user-side sum, so we can
    // split the timed region into driver-time (header parse + socket read)
    // vs sum-time (this callback, which is bench scaffolding, not driver).
    let mutable sumTicks = 0L

    let onBlock (n: int) =
        let t0 = Stopwatch.GetTimestamp()
        let span = col.AsSpan()
        let mutable s = 0UL
        for i in 0 .. n - 1 do s <- s + span.[i]
        totalSum  <- totalSum + s
        totalRows <- totalRows + int64 n
        sumTicks  <- sumTicks + (Stopwatch.GetTimestamp() - t0)

    let q =
        { ChQuery.defaults with
            Body     = sprintf "SELECT number FROM system.numbers_mt LIMIT %d" rows
            Results  = [ { Name = "number"; Column = col } ]
            OnBlock  = onBlock
            Settings = [ { Key = "max_block_size"; Value = string blockSize; Important = true } ] }

    // DIAGNOSTIC: reset the read(2) accumulator after connect so it measures
    // only the timed query — splits driver-time into read(2) (wait + kernel
    // copy) vs client CPU.
    BlockingFdStream.ResetReadStats()
    let sw = Stopwatch.StartNew()
    client.DoAsync(q, cts.Token).GetAwaiter().GetResult()
    sw.Stop()

    let expSum =
        let n = uint64 rows
        n * (n - 1UL) / 2UL
    if totalRows <> rows then
        eprintfn "FAIL: got %d rows, expected %d" totalRows rows; 3
    elif totalSum <> expSum then
        eprintfn "FAIL: got sum %d, expected %d" totalSum expSum; 4
    else
        let bytes = totalRows * 8L
        let gib   = float bytes / 1073741824.0
        let ms    = sw.Elapsed.TotalMilliseconds
        let gbps  = gib / (ms / 1000.0)
        let sumMs = float sumTicks / float Stopwatch.Frequency * 1000.0
        let driverMs = ms - sumMs
        let readMs = float BlockingFdStream.ReadTicks / float Stopwatch.Frequency * 1000.0
        eprintfn "OK: %d rows | %.2f GiB | %.0f ms | %.2f GiB/s | sum %.0f | driver %.0f (read %.0f / %d calls, cpu %.0f)"
            totalRows gib ms gbps sumMs driverMs readMs BlockingFdStream.ReadCount (driverMs - readMs)
        0
