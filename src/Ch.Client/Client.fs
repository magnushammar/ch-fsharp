namespace Ch.Client

open System
open System.IO
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks
open Ch.Proto

/// Caller-owned target for one column. Used both for `Results` (SELECT
/// output) and `Input` (INSERT source) — the shape is identical.
type ColumnResult = {
    /// Optional name. Empty string accepts whatever the server sends at this
    /// index. Otherwise must match the server-reported column name. For
    /// `Input`, the name is sent to the server with the data block.
    Name: string
    /// Receiver/source column. Same instance is reused across blocks.
    Column: IColumnResult
}

/// Type alias for clarity at INSERT call sites.
type ColumnInput = ColumnResult

/// Description of a SELECT or INSERT query. Mirrors `ch.Query` in ch-go.
type ChQuery = {
    /// SQL text.
    Body: string
    /// Optional explicit query id. Defaults to a fresh UUID.
    QueryId: string option
    /// One `ColumnResult` per server column, matched by **index**. Each
    /// entry's `Column` is decoded into in-place every block. Used for
    /// SELECT. Leave empty for INSERT.
    Results: ColumnResult list
    /// One `ColumnInput` per input column. The driver reads
    /// `Column.EncodeColumn` per row block sent to the server. Used for
    /// INSERT. Leave empty for SELECT.
    Input: ColumnInput list
    /// Called once per server `Data` block, *after* all column decodes. The
    /// int argument is the row count of the current block.
    OnBlock: int -> unit
    /// Called *after* each block has been written to the server when
    /// streaming INSERT data. Returns `true` to send another block (refill
    /// the columns first), `false` to terminate. `None` = single block.
    OnInput: (unit -> bool) option
    /// Query-scope settings (e.g. `max_block_size`).
    Settings: Query.Setting list
}

[<RequireQualifiedAccess>]
module ChQuery =
    /// Default `ChQuery` skeleton — fill in `Body` plus the role-specific
    /// fields (`Results`+`OnBlock` for SELECT, `Input`+optional `OnInput`
    /// for INSERT). Callers use record-update syntax:
    /// ```fsharp
    /// { ChQuery.defaults with Body = "..."; Results = [...]; OnBlock = ... }
    /// ```
    let defaults : ChQuery = {
        Body = ""
        QueryId = None
        Results = []
        Input = []
        OnBlock = ignore
        OnInput = None
        Settings = []
    }

/// Single TCP connection to ClickHouse, mirroring `ch.Client` in ch-go.
/// Not thread-safe — same as the Go reference. Use one connection per
/// concurrent query.
[<Sealed>]
type Client private (
    tcp: TcpClient,
    stream: Stream,
    reader: Reader,
    buf: Buf,
    server: ServerHello.T,
    localAddress: string,
    opts: ChOptions) =

    /// Server identification returned during handshake.
    member _.Server : ServerHello.T = server
    member _.LocalAddress = localAddress

    member _.Dispose() =
        try stream.Dispose() with _ -> ()
        try tcp.Dispose() with _ -> ()

    interface IDisposable with
        member this.Dispose() = this.Dispose()

    /// Ping the server. Round-trip a single byte each way.
    member _.PingAsync(ct: CancellationToken) : Task =
        task {
            buf.PutClientCode(ClientCode.Ping)
            do! buf.WriteToAndResetAsync(stream, ct)
            do! stream.FlushAsync(ct)
            let code = reader.ServerCode()
            match code with
            | ServerCode.Pong -> ()
            | ServerCode.Exception -> Exception.decodeAndThrow reader
            | other -> raise (InvalidDataException $"unexpected packet after Ping: {other}")
        }

    /// Append an end-of-data marker block (ClientCodeData + empty table name +
    /// BlockInfo + columns=0 + rows=0) to `buf`. Required after every Query
    /// packet — see `ch.go: encodeBlankBlock`.
    ///
    /// When compression is enabled, the block *body* (everything after the
    /// temp-table-name) must be wrapped in the compressed frame format —
    /// we use method=None since LZ4 encoding for a 10-byte payload would be
    /// pointless.
    member private _.EncodeBlankBlock() =
        buf.PutClientCode(ClientCode.Data)
        buf.PutString("")                  // temp-table name

        let inner = Buf(32)
        // BlockInfo zero (Overflows=false, BucketNum=0)
        inner.PutUVarInt(1UL)
        inner.PutBool(false)
        inner.PutUVarInt(2UL)
        inner.PutInt32(0)
        inner.PutUVarInt(0UL)
        inner.PutInt(0)                    // columns
        inner.PutInt(0)                    // rows

        if opts.Compression then
            CompressedFrame.wrapNone buf inner.WrittenSpan
        else
            buf.PutRaw(inner.WrittenSpan)

    /// Encode one Data block carrying the rows currently in `input`. Mirrors
    /// `client.go: encodeBlock`. Layout:
    ///   ClientCodeData + tempTableName="" + (optionally compressed:)
    ///     BlockInfo + columns + rows + per-column(name, type, custom=false,
    ///     state header if rows>0, body)
    ///
    /// For now the compressed-wrapper method is None (no LZ4) even when
    /// `opts.Compression = true` — matches our `EncodeBlankBlock` choice.
    /// True LZ4 framing for INSERT bodies is a perf follow-up.
    member private _.EncodeDataBlock(input: ColumnInput array) =
        buf.PutClientCode(ClientCode.Data)
        buf.PutString("")

        let body = Buf(1024)
        body.PutUVarInt(1UL); body.PutBool(false)
        body.PutUVarInt(2UL); body.PutInt32(-1)   // BucketNum = -1
        body.PutUVarInt(0UL)
        body.PutInt(input.Length)                  // columns
        let rows = if input.Length = 0 then 0 else input.[0].Column.Rows
        body.PutInt(rows)

        for col in input do
            body.PutString(col.Name)
            body.PutString(col.Column.Type)
            body.PutBool(false)                    // custom serialization = false
            if rows > 0 then
                match col.Column with
                | :? IStatefulColumn as s -> s.EncodeState(body)
                | _ -> ()
                col.Column.EncodeColumn(body)

        if opts.Compression then
            // Real LZ4 framing for data blocks. The blank end-of-data block
            // is small enough that wrapNone (still checksummed) is fine.
            CompressedFrame.wrapLZ4 buf body.WrittenSpan
        else
            buf.PutRaw(body.WrittenSpan)

    /// Stream input blocks to the server. Called after the server's header
    /// block (rows=0) has been decoded and used to drive `Infer` on each
    /// input column. Mirrors `query.go: sendInput`.
    member private this.SendInputAsync(q: ChQuery, ct: CancellationToken) : Task =
        task {
            let inputs = List.toArray q.Input
            // First (or only) data block.
            this.EncodeDataBlock(inputs)

            match q.OnInput with
            | None -> ()
            | Some next ->
                // Multi-block streaming: flush each block, ask for the next.
                do! buf.WriteToAndResetAsync(stream, ct)
                do! stream.FlushAsync(ct)
                let mutable more = next()
                while more do
                    this.EncodeDataBlock(inputs)
                    do! buf.WriteToAndResetAsync(stream, ct)
                    do! stream.FlushAsync(ct)
                    more <- next()

            // End-of-data marker terminates the input stream.
            this.EncodeBlankBlock()
            do! buf.WriteToAndResetAsync(stream, ct)
            do! stream.FlushAsync(ct)
        }

    /// Execute a SELECT query. Receive-only — sends Query + blank end-of-data
    /// marker, then drives the response loop.
    member this.DoAsync(q: ChQuery, ct: CancellationToken) : Task =
        task {
            let qid = q.QueryId |> Option.defaultValue (Guid.NewGuid().ToString())

            // 1. Query packet (`ClientCodeQuery` + id + ClientInfo + settings + body)
            let compressionMode =
                if opts.Compression then Compression.Enabled else Compression.Disabled
            Query.encode buf qid
                (fun b ->
                    ClientInfo.encodeInitial b
                        opts.User
                        qid
                        localAddress
                        ""                              // ClientHostname (empty)
                        opts.ClientName
                        opts.ClientMajor opts.ClientMinor opts.ClientPatch)
                q.Settings
                compressionMode
                q.Body

            // 2. End-of-data marker.
            this.EncodeBlankBlock()

            // 3. Flush both packets in one go. SYNCHRONOUS on purpose — a
            //    `do!` here would resume the receive loop on a threadpool
            //    worker, and when that worker blocks in `read(2)` the pool
            //    injects more threads that spin (sched_yield storm). Running
            //    the whole task inline on the caller's thread avoids that.
            buf.WriteToAndReset(stream)
            stream.Flush()

            // 4. Synchronous receive loop. The hot path is `ServerCode.Data`,
            //    everything else is rare/small.
            //
            //    Mode dispatch: INSERT (`q.Input` non-empty) treats the FIRST
            //    server `Data` block (always rows=0) as a schema header used
            //    to drive `Infer` on each input column. After that we send
            //    our input, and the rest of the loop just drains
            //    Progress/Profile/EndOfStream.
            let isInsert = not q.Input.IsEmpty
            let mutable headerSeen = false
            let mutable stop = false

            // Per-query-invariant state, hoisted out of the receive loop so
            // the target arrays and handler closures are allocated once, not
            // once per block.
            let resultTargets = List.toArray q.Results
            let inputTargets  = List.toArray q.Input
            let mutable colIdx = 0
            let mutable blockRows = 0
            // The schema (column names + types) is identical on every block,
            // so it is fully validated only on the FIRST Data block. After
            // that, `Block.decodeFast` skips materialising the name/type
            // strings and we skip the type-compat regex entirely — that
            // per-block work was ~15k string allocs + ~60k regex ops/run of
            // pure waste.
            let mutable schemaValidated = false
            // Set per slow-path block so the validating handler knows which
            // target set it's checking and whether to decode the body.
            let mutable targets : ColumnResult array = resultTargets
            let mutable insertHeaderMode = false

            // Slow path — first Data block (and the INSERT schema header).
            // Materialises the name/type strings, runs Infer + type-compat.
            let validatingHandler : Block.ColumnHandler =
                fun name typ rowCount ->
                    if targets.Length = 0 then () else
                    if colIdx >= targets.Length then
                        raise (InvalidDataException
                            $"server sent column [{colIdx}] but only {targets.Length} expected")
                    let target = targets.[colIdx]
                    if target.Name <> "" && target.Name <> name then
                        raise (InvalidDataException
                            $"column [{colIdx}] name mismatch: server '{name}', expected '{target.Name}'")
                    // Parameterised columns (Enum8/16, …) infer their mapping
                    // from the server-sent type string before the compat check.
                    match target.Column with
                    | :? IInferable as inf -> inf.Infer(typ)
                    | _ -> ()
                    if not (ColumnType.isCompatible target.Column.Type typ) then
                        raise (InvalidDataException
                            $"column '{name}' type mismatch: server '{typ}', client '{target.Column.Type}'")
                    if not insertHeaderMode then
                        // State header (e.g. LowCardinality) decodes before
                        // the body — but only when the block has rows.
                        if rowCount > 0 then
                            match target.Column with
                            | :? IStatefulColumn as s -> s.DecodeState(reader)
                            | _ -> ()
                        target.Column.DecodeColumn(reader, rowCount)
                        blockRows <- rowCount
                    colIdx <- colIdx + 1

            // Fast path — every Data block after the first. The schema is
            // already validated and `Block.decodeFast` has skipped the
            // name/type strings; this just runs the state header + body
            // decode. Only ever reached for SELECT (the INSERT header always
            // takes the slow path).
            let fastHandler (rowCount: int) =
                let target = resultTargets.[colIdx]
                if rowCount > 0 then
                    match target.Column with
                    | :? IStatefulColumn as s -> s.DecodeState(reader)
                    | _ -> ()
                target.Column.DecodeColumn(reader, rowCount)
                blockRows <- rowCount
                colIdx <- colIdx + 1

            while not stop do
                let code = reader.ServerCode()
                match code with
                | ServerCode.Data
                | ServerCode.Totals
                | ServerCode.Extremes ->
                    // Temp-table-name lives OUTSIDE the compressed block frame
                    // (see ch-go `client.go:226-234`).
                    let tempTable = reader.Str()
                    if tempTable <> "" then
                        raise (InvalidDataException
                            $"unexpected temp-table name '{tempTable}'")

                    let insertHeader = isInsert && not headerSeen
                    let slowPath = insertHeader || not schemaValidated
                    colIdx <- 0
                    blockRows <- 0
                    if slowPath then
                        // INSERT header validates against the input columns;
                        // SELECT against the result columns.
                        targets <- if insertHeader then inputTargets else resultTargets
                        insertHeaderMode <- insertHeader

                    if opts.Compression then
                        reader.EnableCompression()
                        try
                            if slowPath then Block.decode reader validatingHandler |> ignore
                            else Block.decodeFast reader fastHandler |> ignore
                        finally reader.DisableCompression()
                    else
                        if slowPath then Block.decode reader validatingHandler |> ignore
                        else Block.decodeFast reader fastHandler |> ignore

                    // A validated SELECT block latches the fast path for the
                    // rest of the query. INSERT keeps the slow path (it only
                    // ever has the one header block).
                    if slowPath && not insertHeader then schemaValidated <- true

                    if isInsert && not headerSeen then
                        // Server header consumed; now stream our input.
                        headerSeen <- true
                        do! this.SendInputAsync(q, ct)
                    elif blockRows > 0 then
                        q.OnBlock(blockRows)

                | ServerCode.Progress ->
                    Progress.decodeAndIgnore reader

                | ServerCode.Profile ->
                    Profile.decodeAndIgnore reader

                | ServerCode.TableColumns ->
                    TableColumns.decodeAndIgnore reader

                | ServerCode.Log
                | ServerCode.ProfileEvents ->
                    // Log / ProfileEvents are blocks but NOT compressible
                    // (`proto/server_code.go: Compressible`). They share the
                    // temp-table-name + block-body shape, both uncompressed.
                    let tempTable = reader.Str()
                    if tempTable <> "" then
                        raise (InvalidDataException
                            $"unexpected temp-table name '{tempTable}'")
                    let skipHandler : Block.ColumnHandler =
                        fun _name typ rowCount -> ColumnSkip.skip reader typ rowCount
                    Block.decode reader skipHandler |> ignore

                | ServerCode.EndOfStream ->
                    stop <- true

                | ServerCode.Exception ->
                    Exception.decodeAndThrow reader

                | other ->
                    raise (InvalidDataException $"unexpected server packet: {other}")
        }

    /// Connect to a ClickHouse server and perform the protocol handshake.
    /// Requires server revision >= 54460.
    static member ConnectAsync(opts: ChOptions, ct: CancellationToken) : Task<Client> =
        task {
            let struct (host, port) = ChOptions.parseHostPort opts.Address

            let tcp = new TcpClient()
            tcp.NoDelay <- true

            // SYNCHRONOUS connect — on purpose. `ConnectAsync` registers the
            // fd with .NET's `SocketAsyncEngine` (epoll). After that, because
            // the server streams continuously, the fd is permanently
            // readable and the engine's epoll thread busy-loops
            // (`epoll_wait` returns instantly forever, finds no pending .NET
            // async op since we read via raw `read(2)`, re-arms, repeats) —
            // a `sched_yield` storm. A sync connect never engages the engine,
            // so it never watches the fd. The whole connect + handshake then
            // runs inline on the caller's thread; nothing touches the
            // threadpool.
            tcp.Connect(host, port)
            tcp.ReceiveBufferSize <- 16 * 1024 * 1024

            // The fd is driven by true blocking `read(2)` / `write(2)`
            // (BlockingFdStream) — the kernel parks the thread in-kernel
            // until data arrives, exactly like Go's netpoller parks a
            // goroutine. The Reader owns an 8 KB buffer for the tiny header
            // reads; column bodies drain it then read straight into the
            // destination column buffer — no double-copy, no intermediate
            // BufferedStream layer. The same handle drives writes.
            let fd = int (tcp.Client.Handle)
            let stream : Stream = new BlockingFdStream(fd)
            let reader = Reader(stream)
            let buf = Buf(512)

            // 1. Send ClientHello — synchronous, see above.
            ClientHello.encode buf opts.ClientName
                opts.ClientMajor opts.ClientMinor Protocol.Version
                opts.Database opts.User opts.Password
            buf.WriteToAndReset(stream)
            stream.Flush()

            // 2. Read response: Hello or Exception
            let code = reader.ServerCode()
            match code with
            | ServerCode.Hello ->
                let server = ServerHello.decode reader
                if server.Revision < Protocol.Version then
                    tcp.Dispose()
                    raise (InvalidOperationException
                        $"server revision {server.Revision} below required {Protocol.Version}")

                // 3. Send addendum (`proto/feature.go`, FeatureAddendum=54458).
                //    Quota key, empty.
                buf.PutString("")
                buf.WriteToAndReset(stream)
                stream.Flush()

                let localAddr = $"{tcp.Client.LocalEndPoint}"

                return new Client(tcp, stream, reader, buf, server, localAddr, opts)

            | ServerCode.Exception ->
                Exception.decodeAndThrow reader
                return Unchecked.defaultof<Client>

            | other ->
                tcp.Dispose()
                return raise (InvalidDataException $"handshake: unexpected packet {other}")
        }
