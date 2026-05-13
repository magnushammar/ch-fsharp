namespace Ch.Proto

/// Packet codes sent by the client. See `proto/client_code.go` in ch-go.
type ClientCode =
    | Hello                = 0uy
    | Query                = 1uy
    | Data                 = 2uy
    | Cancel               = 3uy
    | Ping                 = 4uy
    | TablesStatusRequest  = 5uy
    | SSHChallengeRequest  = 11uy
    | SSHChallengeResponse = 12uy

/// Packet codes sent by the server. See `proto/server_code.go` in ch-go.
type ServerCode =
    | Hello            = 0uy
    | Data             = 1uy
    | Exception        = 2uy
    | Progress         = 3uy
    | Pong             = 4uy
    | EndOfStream      = 5uy
    | Profile          = 6uy
    | Totals           = 7uy
    | Extremes         = 8uy
    | TablesStatus     = 9uy
    | Log              = 10uy
    | TableColumns     = 11uy
    | PartUUIDs        = 12uy
    | ReadTaskRequest  = 13uy
    | ProfileEvents    = 14uy
    | SSHChallenge     = 18uy

/// Query stage. See `proto/stage.go`.
type Stage =
    | FetchColumns    = 0uy
    | WithMergeable   = 1uy
    | Complete        = 2uy

/// Protocol-level compression setting (independent of compression *method*).
/// See `proto/compression.go`.
type Compression =
    | Disabled = 0uy
    | Enabled  = 1uy

/// ClickHouse TCP binary protocol version this client advertises.
/// See `proto/proto.go` in ch-go (`Version = 54460`).
[<RequireQualifiedAccess>]
module Protocol =
    [<Literal>]
    let Version = 54460

    [<Literal>]
    let Name = "clickhouse/ch-fsharp"
