namespace Ch.Client

/// Connection options for a single ClickHouse TCP connection.
/// Mirrors ch-go's `ch.Options` but only the fields the MVP uses.
type ChOptions = {
    /// "host:port", default "127.0.0.1:9000".
    Address: string
    /// Database name. Defaults to "default".
    Database: string
    /// User name. Defaults to "default".
    User: string
    /// Password. Defaults to "" (no auth).
    Password: string
    /// Client identification string sent in ClientHello / ClientInfo.
    ClientName: string
    /// Client major version reported to the server (cosmetic).
    ClientMajor: int
    ClientMinor: int
    ClientPatch: int
    /// Enable LZ4 compression of Data/Totals/Extremes packets. Defaults to false.
    Compression: bool
}

[<RequireQualifiedAccess>]
module ChOptions =
    let defaults = {
        Address = "127.0.0.1:9000"
        Database = "default"
        User = "default"
        Password = ""
        ClientName = "clickhouse/ch-fsharp"
        ClientMajor = 1
        ClientMinor = 0
        ClientPatch = 0
        Compression = false
    }

    let parseHostPort (addr: string) : struct (string * int) =
        let idx = addr.LastIndexOf(':')
        if idx < 0 then struct (addr, 9000)
        else
            let host = addr.Substring(0, idx)
            let port =
                match System.Int32.TryParse(addr.Substring(idx + 1)) with
                | true, p -> p
                | _ -> 9000
            struct (host, port)
