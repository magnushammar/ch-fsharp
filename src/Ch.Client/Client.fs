namespace Ch.Client

open System
open System.IO
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks
open Ch.Proto

/// Caller-owned result target for one column in the SELECT.
type ColumnResult = {
    /// Optional name. Empty string accepts whatever the server sends at this
    /// index. Otherwise must match the server-reported column name.
    Name: string
    /// Receiver column. Same instance is reused across blocks.
    Column: IColumnResult
}

/// Description of a SELECT query. Mirrors `ch.Query` in ch-go but without
/// the INSERT/OnInput surface (read-only for now).
type ChQuery = {
    /// SQL text.
    Body: string
    /// Optional explicit query id. Defaults to a fresh UUID.
    QueryId: string option
    /// One ColumnResult per server column, matched by **index**. Each entry's
    /// `Column` is decoded into in-place every block.
    Results: ColumnResult list
    /// Called once per server `Data` block, *after* all column decodes. The
    /// int argument is the row count of the current block.
    OnBlock: int -> unit
    /// Query-scope settings (e.g. `max_block_size`).
    Settings: Query.Setting list
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

            // 3. Flush both packets in one go.
            do! buf.WriteToAndResetAsync(stream, ct)
            do! stream.FlushAsync(ct)

            // 4. Synchronous receive loop. The hot path is `ServerCode.Data`,
            //    everything else is rare/small.
            let mutable stop = false
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

                    let results = List.toArray q.Results
                    let mutable colIdx = 0
                    let mutable blockRows = 0

                    let handler : Block.ColumnHandler =
                        fun name typ rowCount ->
                            if colIdx >= results.Length then
                                raise (InvalidDataException
                                    $"server sent column [{colIdx}] but only {results.Length} expected")
                            let target = results.[colIdx]
                            if target.Name <> "" && target.Name <> name then
                                raise (InvalidDataException
                                    $"column [{colIdx}] name mismatch: server '{name}', expected '{target.Name}'")
                            if not (ColumnType.isCompatible target.Column.Type typ) then
                                raise (InvalidDataException
                                    $"column '{name}' type mismatch: server '{typ}', client '{target.Column.Type}'")
                            // State header (e.g. LowCardinality) decodes before
                            // the body — but only when the block has rows
                            // (ch-go `Results.DecodeResult`: `if b.Rows == 0
                            // then continue` skips state+body).
                            if rowCount > 0 then
                                match target.Column with
                                | :? IStatefulColumn as s -> s.DecodeState(reader)
                                | _ -> ()
                            target.Column.DecodeColumn(reader, rowCount)
                            blockRows <- rowCount
                            colIdx <- colIdx + 1

                    if opts.Compression then
                        reader.EnableCompression()
                        try Block.decode reader handler |> ignore
                        finally reader.DisableCompression()
                    else
                        Block.decode reader handler |> ignore

                    if blockRows > 0 then q.OnBlock(blockRows)

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
            do! tcp.ConnectAsync(host, port, ct)

            let net = tcp.GetStream()
            let stream : Stream = new BufferedStream(net, 128 * 1024)
            let reader = Reader(stream)
            let buf = Buf(512)

            // 1. Send ClientHello
            ClientHello.encode buf opts.ClientName
                opts.ClientMajor opts.ClientMinor Protocol.Version
                opts.Database opts.User opts.Password
            do! buf.WriteToAndResetAsync(stream, ct)
            do! stream.FlushAsync(ct)

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
                do! buf.WriteToAndResetAsync(stream, ct)
                do! stream.FlushAsync(ct)

                let localAddr = $"{tcp.Client.LocalEndPoint}"

                return new Client(tcp, stream, reader, buf, server, localAddr, opts)

            | ServerCode.Exception ->
                Exception.decodeAndThrow reader
                return Unchecked.defaultof<Client>

            | other ->
                tcp.Dispose()
                return raise (InvalidDataException $"handshake: unexpected packet {other}")
        }
