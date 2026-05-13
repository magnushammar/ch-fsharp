namespace Ch.Proto

open System
open System.IO

/// Server-side error wrapped from `ServerCodeException`.
/// Mirrors `proto/exception.go` and the higher-level wrapper in `ch.go`.
type ClickHouseServerException(code: int32, name: string, message: string, stack: string, nested: bool) =
    inherit Exception(message)
    member _.Code = code
    member _.Name = name
    member _.Stack = stack
    member _.Nested = nested
    override this.Message =
        $"ClickHouse {name} ({code}): {message}"

[<RequireQualifiedAccess>]
module ClientHello =
    /// Encodes a `ClientCodeHello` packet at the start of the handshake.
    /// See `proto/client_hello.go:24`.
    let encode (b: Buf)
               (name: string)
               (major: int) (minor: int) (protocolVersion: int)
               (database: string) (user: string) (password: string) =
        b.PutClientCode(ClientCode.Hello)
        b.PutString(name)
        b.PutInt(major)
        b.PutInt(minor)
        b.PutInt(protocolVersion)
        b.PutString(database)
        b.PutString(user)
        b.PutString(password)

[<RequireQualifiedAccess>]
module ServerHello =
    /// Decoded form of `ServerCodeHello`. See `proto/server_hello.go`.
    type T = {
        Name: string
        Major: int
        Minor: int
        Revision: int
        Timezone: string
        DisplayName: string
        Patch: int
    }

    /// Decode at protocol version v >= 54460 (all sub-fields present).
    /// `FeatureTimezone=54058`, `FeatureDisplayName=54372`,
    /// `FeatureVersionPatch=54401`.
    let decode (r: Reader) : T =
        let name = r.Str()
        let major = r.Int()
        let minor = r.Int()
        let revision = r.Int()
        // All three features are below the v54460 floor we require.
        let timezone = r.Str()
        let displayName = r.Str()
        let patch = r.Int()
        {
            Name = name; Major = major; Minor = minor; Revision = revision
            Timezone = timezone; DisplayName = displayName; Patch = patch
        }

[<RequireQualifiedAccess>]
module ClientInfo =
    /// Hard-coded encode for protocol v54460. Every feature gate in
    /// `proto/client_info.go` evaluates to true at this version.
    ///
    /// Fields in wire order (verified against `proto/client_info.go:65`):
    ///   Query(byte=1), InitialUser(str), InitialQueryID(str),
    ///   InitialAddress(str), InitialTime(int64 LE), Interface(byte=1=TCP),
    ///   OSUser(str), ClientHostname(str), ClientName(str),
    ///   Major(uvarint), Minor(uvarint), ProtocolVersion(uvarint),
    ///   QuotaKey(str), DistributedDepth(uvarint=0), Patch(uvarint),
    ///   HasOpenTelemetrySpan(byte=0),
    ///   CollaborateWithInitiator(uvarint=0),
    ///   CountParticipatingReplicas(uvarint=0),
    ///   NumberOfCurrentReplica(uvarint=0).
    let encodeInitial (b: Buf)
                      (initialUser: string)
                      (initialQueryId: string)
                      (initialAddress: string)
                      (clientHostname: string)
                      (clientName: string)
                      (major: int) (minor: int) (patch: int) =
        b.PutByte(1uy)                                 // ClientQueryKind = Initial
        b.PutString(initialUser)
        b.PutString(initialQueryId)
        b.PutString(initialAddress)
        b.PutInt64(0L)                                 // InitialTime (microseconds since epoch) — leave 0
        b.PutByte(1uy)                                 // Interface = TCP
        b.PutString("")                                // OSUser
        b.PutString(clientHostname)
        b.PutString(clientName)
        b.PutInt(major)
        b.PutInt(minor)
        b.PutInt(Protocol.Version)
        b.PutString("")                                // QuotaKey
        b.PutInt(0)                                    // DistributedDepth
        b.PutInt(patch)                                // Patch (only when Interface=TCP)
        b.PutByte(0uy)                                 // OpenTelemetry: no span
        b.PutInt(0)                                    // CollaborateWithInitiator = 0
        b.PutInt(0)                                    // CountParticipatingReplicas
        b.PutInt(0)                                    // NumberOfCurrentReplica

[<RequireQualifiedAccess>]
module Query =
    /// One `Setting` row inside a Query packet. ch-go `proto/query.go:50`.
    type Setting = { Key: string; Value: string; Important: bool }

    [<Literal>]
    let private FlagImportant = 0x01UL

    let private encodeSetting (b: Buf) (s: Setting) =
        b.PutString(s.Key)
        let flags = if s.Important then FlagImportant else 0UL
        b.PutUVarInt(flags)
        b.PutString(s.Value)

    /// Encode a full `ClientCodeQuery` packet at v54460. Always sends
    /// `Stage=Complete`. `compression` controls whether the server will send
    /// Data/Totals/Extremes blocks wrapped in the compressed frame format.
    let encode (b: Buf)
               (queryId: string)
               (encodeClientInfo: Buf -> unit)
               (settings: Setting seq)
               (compression: Compression)
               (body: string) =
        b.PutClientCode(ClientCode.Query)
        b.PutString(queryId)
        encodeClientInfo b                              // ClientInfo (v54460 shape)
        for s in settings do encodeSetting b s
        b.PutString("")                                 // end of settings
        b.PutString("")                                 // inter-server secret (empty)
        b.PutUVarInt(uint64 (byte Stage.Complete))
        b.PutUVarInt(uint64 (byte compression))
        b.PutString(body)
        b.PutString("")                                 // end of parameters

[<RequireQualifiedAccess>]
module Exception =
    /// Decode a single `proto.Exception` payload (without the leading
    /// ServerCode byte — that was already consumed by the dispatcher).
    /// See `proto/exception.go`.
    let private decodeOne (r: Reader) : struct (int32 * string * string * string * bool) =
        let code = r.Int32()
        let name = r.Str()
        let message = r.Str()
        let stack = r.Str()
        let nested = r.Bool()
        struct (code, name, message, stack, nested)

    /// Decode a chain of exceptions (the server can emit a list when one
    /// exception wraps another). Throws the head exception.
    let decodeAndThrow (r: Reader) : 'a =
        let struct (code, name, message, stack, nested) = decodeOne r
        let mutable more = nested
        // Drain any nested chain so the parser stays in sync.
        while more do
            let struct (_, _, _, _, n) = decodeOne r
            more <- n
        raise (ClickHouseServerException(code, name, message, stack, false))

[<RequireQualifiedAccess>]
module Progress =
    /// Decode and ignore. See `proto/progress.go`. All three branches fire
    /// at v54460 (`FeatureClientWriteInfo=54420`,
    /// `FeatureServerQueryTimeInProgress=54460`).
    let decodeAndIgnore (r: Reader) =
        let _rows = r.UVarInt()
        let _bytes = r.UVarInt()
        let _totalRows = r.UVarInt()
        let _wroteRows = r.UVarInt()
        let _wroteBytes = r.UVarInt()
        let _elapsedNs = r.UVarInt()
        ()

[<RequireQualifiedAccess>]
module Profile =
    /// Decode and ignore. See `proto/profile.go`.
    let decodeAndIgnore (r: Reader) =
        let _rows = r.UVarInt()
        let _blocks = r.UVarInt()
        let _bytes = r.UVarInt()
        let _appliedLimit = r.Bool()
        let _rowsBeforeLimit = r.UVarInt()
        let _calculated = r.Bool()
        ()

[<RequireQualifiedAccess>]
module TableColumns =
    /// `ServerCodeTableColumns` is just two strings — *not* a block.
    /// See `proto/table_columns.go`.
    let decodeAndIgnore (r: Reader) =
        let _first = r.Str()
        let _second = r.Str()
        ()
