namespace Ch.Client

open System
open System.IO
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks
open Ch.Proto

/// Description of a SELECT query. MVP scope: exactly one result column of
/// type `UInt64`.
type ChQuery = {
    /// SQL text.
    Body: string
    /// Optional explicit query id. Defaults to a fresh UUID.
    QueryId: string option
    /// Caller-owned column instance that receives row data. The same instance
    /// is reused across blocks; read it inside `OnBlock` via `Result.AsSpan()`.
    Result: ColUInt64
    /// Called once per server `Data` block after `Result.DecodeColumn`. The
    /// integer argument is the row count of the current block.
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
    member private _.EncodeBlankBlock() =
        buf.PutClientCode(ClientCode.Data)
        buf.PutString("")                  // temp-table name
        // BlockInfo zero (Overflows=false, BucketNum=0)
        buf.PutUVarInt(1UL)
        buf.PutBool(false)
        buf.PutUVarInt(2UL)
        buf.PutInt32(0)
        buf.PutUVarInt(0UL)
        buf.PutInt(0)                      // columns
        buf.PutInt(0)                      // rows

    /// Execute a SELECT query. Receive-only — sends Query + blank end-of-data
    /// marker, then drives the response loop.
    member this.DoAsync(q: ChQuery, ct: CancellationToken) : Task =
        task {
            let qid = q.QueryId |> Option.defaultValue (Guid.NewGuid().ToString())

            // 1. Query packet (`ClientCodeQuery` + id + ClientInfo + settings + body)
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
                    let mutable blockRows = 0
                    let handler : Block.ColumnHandler =
                        fun _name typ rowCount ->
                            if typ = "UInt64" then
                                q.Result.DecodeColumn(reader, rowCount)
                                blockRows <- rowCount
                            else
                                raise (InvalidDataException
                                    $"MVP supports UInt64 only, got column type '{typ}'")
                    let struct (_, _) = Block.decode reader handler
                    if blockRows > 0 then q.OnBlock(blockRows)

                | ServerCode.Progress ->
                    Progress.decodeAndIgnore reader

                | ServerCode.Profile ->
                    Profile.decodeAndIgnore reader

                | ServerCode.TableColumns ->
                    TableColumns.decodeAndIgnore reader

                | ServerCode.Log
                | ServerCode.ProfileEvents ->
                    // Drain the block bytes so the parser stays in sync.
                    let skipHandler : Block.ColumnHandler =
                        fun _name typ rowCount -> ColumnSkip.skip reader typ rowCount
                    let struct (_, _) = Block.decode reader skipHandler
                    ()

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
