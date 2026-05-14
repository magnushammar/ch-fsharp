namespace Ch.Proto

open System
open System.IO
open System.Runtime.InteropServices
open Microsoft.FSharp.NativeInterop

#nowarn "9"   // unverifiable IL — required for fixed / NativePtr

/// A `Stream` over a raw file descriptor that does **true blocking** `read(2)`
/// / `write(2)` syscalls.
///
/// Why this exists: on Linux, .NET keeps every socket fd in non-blocking mode
/// and routes I/O through its epoll-based `SocketAsyncEngine`. A *synchronous*
/// `Socket.Receive` that hits `EAGAIN` enters `SocketAsyncContext`'s sync wait,
/// which **spin-waits** (`sched_yield` storm — ~225 k calls on a 500 M-row
/// read) before parking. There is no runtime switch to disable that spin.
///
/// A blocking `read(2)` instead parks the calling thread in-kernel until data
/// arrives — exactly what Go's netpoller does for a parked goroutine. Zero
/// `sched_yield`, zero epoll round-trips on the hot path.
///
/// Only safe when nothing else touches the fd concurrently — i.e. after the
/// async connect completes and the caller owns the fd for the rest of the
/// connection. The constructor flips the fd back to blocking mode.
[<Sealed>]
type BlockingFdStream(fd: int) =
    inherit Stream()

    [<Literal>]
    static let EINTR = 4
    [<Literal>]
    static let F_GETFL = 3
    [<Literal>]
    static let F_SETFL = 4
    [<Literal>]
    static let O_NONBLOCK = 0x800   // Linux value

    [<DllImport("libc", SetLastError = true)>]
    static extern nativeint read(int fd, nativeint buf, unativeint count)

    [<DllImport("libc", SetLastError = true)>]
    static extern nativeint write(int fd, nativeint buf, unativeint count)

    [<DllImport("libc", SetLastError = true)>]
    static extern int fcntl(int fd, int cmd, int arg)

    do
        // Clear O_NONBLOCK so read/write block in-kernel instead of EAGAIN.
        let flags = fcntl (fd, F_GETFL, 0)
        if flags <> -1 then
            fcntl (fd, F_SETFL, flags &&& ~~~O_NONBLOCK) |> ignore

    // DIAGNOSTIC: process-global accumulators for time spent inside the
    // read(2) syscall (wait-for-data + kernel→userspace copy) and the call
    // count. Lets a bench split driver-time into read(2) vs client CPU.
    // Reset with ResetReadStats before the timed region.
    static let mutable readTicks : int64 = 0L
    static let mutable readCount : int64 = 0L
    static member ReadTicks = readTicks
    static member ReadCount = readCount
    static member ResetReadStats() = readTicks <- 0L; readCount <- 0L

    member _.Fd = fd

    override _.CanRead = true
    override _.CanWrite = true
    override _.CanSeek = false
    override _.Length = raise (NotSupportedException())
    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())
    override _.Flush() = ()
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength(_) = raise (NotSupportedException())

    override _.Read(buffer: Span<byte>) : int =
        if buffer.IsEmpty then 0
        else
            use p = fixed buffer
            let addr = NativePtr.toNativeInt p
            let t0 = System.Diagnostics.Stopwatch.GetTimestamp()
            let mutable n = read (fd, addr, unativeint buffer.Length)
            while n < 0n && Marshal.GetLastPInvokeError() = EINTR do
                n <- read (fd, addr, unativeint buffer.Length)
            readTicks <- readTicks + (System.Diagnostics.Stopwatch.GetTimestamp() - t0)
            readCount <- readCount + 1L
            if n < 0n then
                raise (IOException $"read(2) failed: errno {Marshal.GetLastPInvokeError()}")
            int n

    override this.Read(buffer: byte array, offset: int, count: int) : int =
        this.Read(buffer.AsSpan(offset, count))

    override _.Write(buffer: ReadOnlySpan<byte>) : unit =
        use p = fixed buffer
        let baseAddr = NativePtr.toNativeInt p
        let mutable off = 0
        while off < buffer.Length do
            let addr = baseAddr + nativeint off
            let mutable n = write (fd, addr, unativeint (buffer.Length - off))
            while n < 0n && Marshal.GetLastPInvokeError() = EINTR do
                n <- write (fd, addr, unativeint (buffer.Length - off))
            if n < 0n then
                raise (IOException $"write(2) failed: errno {Marshal.GetLastPInvokeError()}")
            off <- off + int n

    override this.Write(buffer: byte array, offset: int, count: int) : unit =
        this.Write(ReadOnlySpan(buffer, offset, count))
