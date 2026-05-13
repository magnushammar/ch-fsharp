module Ch.Bench.Numbers.Program

open System
open System.Diagnostics
open System.Threading
open Ch.Proto
open Ch.Client

let private parseArgs (argv: string array) =
    let mutable rows = 500_000_000L
    let mutable blockSize = 65536
    let mutable addr = "127.0.0.1:9000"
    let mutable user = "default"
    let mutable database = "default"
    let mutable pingOnly = false
    let mutable quiet = false
    let mutable verifyOnly = false
    let mutable compression = false
    let mutable i = 0
    while i < argv.Length do
        match argv.[i] with
        | "--rows" -> rows <- int64 argv.[i + 1]; i <- i + 2
        | "--block-size" -> blockSize <- int argv.[i + 1]; i <- i + 2
        | "--addr" -> addr <- argv.[i + 1]; i <- i + 2
        | "--user" -> user <- argv.[i + 1]; i <- i + 2
        | "--database" -> database <- argv.[i + 1]; i <- i + 2
        | "--ping" -> pingOnly <- true; i <- i + 1
        | "--quiet" -> quiet <- true; i <- i + 1
        | "--verify" -> verifyOnly <- true; i <- i + 1
        | "--lz4" -> compression <- true; i <- i + 1
        | "--mixed" -> rows <- -1L; i <- i + 1   // sentinel: run a mixed-type smoke instead
        | "--help" | "-h" ->
            eprintfn "Usage: ch-bench-numbers [--addr host:port] [--user u] [--database d]"
            eprintfn "                        [--rows N] [--block-size N]"
            eprintfn "                        [--ping] [--quiet] [--verify]"
            eprintfn ""
            eprintfn "Reads CLICKHOUSE_PASSWORD from the environment."
            exit 0
        | other ->
            eprintfn "unknown arg: %s" other
            exit 2
    rows, blockSize, addr, user, database, pingOnly, quiet, verifyOnly, compression

/// Expected sum of `0 + 1 + ... + N-1` for N rows from `system.numbers_mt LIMIT N`.
let private expectedSum (n: int64) : uint64 =
    if n <= 0L then 0UL
    else
        let n64 = uint64 n
        n64 * (n64 - 1UL) / 2UL

[<EntryPoint>]
let main argv =
    let rows, blockSize, addr, user, database, pingOnly, quiet, _verify, compression = parseArgs argv

    let password =
        match Environment.GetEnvironmentVariable("CLICKHOUSE_PASSWORD") with
        | null -> ""
        | s -> s

    let opts =
        { ChOptions.defaults with
            Address = addr
            Database = database
            User = user
            Password = password
            ClientName = "clickhouse/ch-fsharp.bench-numbers"
            Compression = compression }

    use cts = new CancellationTokenSource()
    let ct = cts.Token

    try
        use client = Client.ConnectAsync(opts, ct).GetAwaiter().GetResult()

        if not quiet then
            eprintfn "Connected: %s rev=%d tz=%s"
                client.Server.Name client.Server.Revision client.Server.Timezone

        if pingOnly then
            client.PingAsync(ct).GetAwaiter().GetResult()
            if not quiet then eprintfn "Pong"
            0
        elif rows = -1L then
            // Mixed-types smoke: exercises every composite + primitive
            // dispatch path including state header (LowCardinality),
            // Nullable mask, and Array offsets.
            let n32 = ColInt32()
            let s   = ColStr()
            let lc  = ColLowCardinality<string>(ColStr())
            let nu  = ColNullable<int32>(ColInt32())
            let ar  = ColArr<int32>(ColInt32())
            let q : ChQuery = {
                Body =
                    "SELECT toInt32(number) AS n32, " +
                    "toString(number) AS s, " +
                    "toLowCardinality(toString(number % 3)) AS lc, " +
                    "if(number % 2 = 0, NULL, toInt32(number)) AS nu, " +
                    "arrayMap(x -> toInt32(x), range(toUInt8(number % 4))) AS ar " +
                    "FROM system.numbers_mt LIMIT 6"
                QueryId = None
                Results = [
                    { Name = "n32"; Column = n32 }
                    { Name = "s";   Column = s   }
                    { Name = "lc";  Column = lc  }
                    { Name = "nu";  Column = nu  }
                    { Name = "ar";  Column = ar  }
                ]
                OnBlock = fun rows ->
                    for i in 0 .. rows - 1 do
                        let nuStr =
                            match nu.Row(i) with
                            | ValueSome v -> string v
                            | ValueNone -> "NULL"
                        let arStr =
                            ar.Row(i)
                            |> Array.map string
                            |> String.concat ","
                        printfn "%d | %s | %s | %s | [%s]"
                            (n32.Row(i)) (s.Row(i)) (lc.Row(i)) nuStr arStr
                Settings = []
            }
            client.DoAsync(q, ct).GetAwaiter().GetResult()
            0
        else
            let col = ColUInt64()
            let mutable totalSum = 0UL
            let mutable totalRows = 0L

            let onBlock (n: int) =
                let span = col.AsSpan()
                let mutable s = 0UL
                for i in 0 .. n - 1 do
                    s <- s + span.[i]
                totalSum <- totalSum + s
                totalRows <- totalRows + int64 n

            let q : ChQuery = {
                Body = sprintf "SELECT number FROM system.numbers_mt LIMIT %d" rows
                QueryId = None
                Results = [ { Name = "number"; Column = col } ]
                OnBlock = onBlock
                Settings = [
                    { Key = "max_block_size"; Value = string blockSize; Important = true }
                ]
            }

            let sw = Stopwatch.StartNew()
            client.DoAsync(q, ct).GetAwaiter().GetResult()
            sw.Stop()

            // Correctness checks.
            let expRows = rows
            let expSum = expectedSum rows
            let mutable rc = 0
            if totalRows <> expRows then
                eprintfn "FAIL: got %d rows, expected %d" totalRows expRows
                rc <- 3
            elif totalSum <> expSum then
                eprintfn "FAIL: got sum %d, expected %d" totalSum expSum
                rc <- 4

            if not quiet && rc = 0 then
                let bytes = totalRows * 8L
                let gib = float bytes / 1073741824.0
                let ms = sw.Elapsed.TotalMilliseconds
                let gbPerSec = gib / (ms / 1000.0)
                eprintfn "OK: %d rows | %.2f GiB | %.0f ms | %.2f GiB/s | sum=%d"
                    totalRows gib ms gbPerSec totalSum

            rc
    with
    | ex ->
        eprintfn "ERROR: %s" ex.Message
        eprintfn "%s" (string ex.StackTrace)
        1
