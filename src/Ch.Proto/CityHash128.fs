namespace Ch.Proto

open System
open System.Buffers.Binary

/// 128-bit hash result. Encoded on the wire as Low (8 bytes LE) followed by
/// High (8 bytes LE) — see `proto/reader.go: city.U128` in ch-go.
[<Struct>]
type U128 = { Low: uint64; High: uint64 }

/// ClickHouse's CityHash128 variant (a port of `go-faster/city.CH128`).
/// Used as the per-block checksum in the compressed wire format.
///
/// Reference: `reference/ch-go`-side `github.com/go-faster/city@v1.0.1`,
/// files `ch_128.go`, `ch_64.go`, `64.go`, `32.go`, `128.go`.
[<RequireQualifiedAccess>]
module CityHash128 =

    let private k0 = 0xc3a5c85c97cb3127UL
    let private k1 = 0xb492b66fbe98f273UL
    let private k2 = 0x9ae16a3b2f90404fUL
    let private k3 = 0xc949d7c7509e6557UL

    let inline private fetch64 (s: ReadOnlySpan<byte>) =
        BinaryPrimitives.ReadUInt64LittleEndian(s)
    let inline private fetch32 (s: ReadOnlySpan<byte>) =
        BinaryPrimitives.ReadUInt32LittleEndian(s)

    /// Bitwise right rotate of `v` by `shift` bits. `shift` must be in 0..63.
    let inline private rot64 (v: uint64) (shift: int) =
        if shift = 0 then v else (v >>> shift) ||| (v <<< (64 - shift))

    let inline private shiftMix (v: uint64) = v ^^^ (v >>> 47)

    let inline private hash128to64 (low: uint64) (high: uint64) =
        let mul = 0x9ddfea08eb382d69UL
        let mutable a = (low ^^^ high) * mul
        a <- a ^^^ (a >>> 47)
        let mutable b = (high ^^^ a) * mul
        b <- b ^^^ (b >>> 47)
        b * mul

    let inline private ch16 (u: uint64) (v: uint64) = hash128to64 u v

    /// 8-byte hash for 0..16 byte inputs (ch-go: `ch_64.go: ch0to16`).
    let private ch0to16 (s: ReadOnlySpan<byte>) : uint64 =
        let length = s.Length
        if length > 8 then
            let a = fetch64 s
            let b = fetch64 (s.Slice(length - 8))
            (ch16 a (rot64 (b + uint64 length) length)) ^^^ b
        elif length >= 4 then
            let a = uint64 (fetch32 s)
            ch16 (uint64 length + (a <<< 3)) (uint64 (fetch32 (s.Slice(length - 4))))
        elif length > 0 then
            let a = uint32 s.[0]
            let b = uint32 s.[length >>> 1]
            let c = uint32 s.[length - 1]
            let y = a + (b <<< 8)
            let z = uint32 length + (c <<< 2)
            shiftMix ((uint64 y * k2) ^^^ (uint64 z * k3)) * k2
        else
            k2

    /// 16-byte hash for 48 input bytes plus two seeds `a` and `b`.
    let inline private weakHash32Seeds (w: uint64) (x: uint64) (y: uint64) (z: uint64) (a0: uint64) (b0: uint64) =
        let mutable a = a0 + w
        let mutable b = rot64 (b0 + a + z) 21
        let c = a
        a <- a + x
        a <- a + y
        b <- b + rot64 a 44
        { Low = a + z; High = b + c }

    let inline private weakHash32SeedsByte (s: ReadOnlySpan<byte>) (a: uint64) (b: uint64) =
        weakHash32Seeds (fetch64 s) (fetch64 (s.Slice(8)))
                        (fetch64 (s.Slice(16))) (fetch64 (s.Slice(24)))
                        a b

    /// `ch_128.go: chMurmur`. Used when CH128Seed sees an input < 128 bytes.
    let private chMurmur (s: ReadOnlySpan<byte>) (seed: U128) : U128 =
        let length = s.Length
        let mutable a = seed.Low
        let mutable b = seed.High
        let mutable c = 0UL
        let mutable d = 0UL
        let mutable l = length - 16
        let mutable rest = s
        if length <= 16 then
            a <- shiftMix (a * k1) * k1
            c <- b * k1 + ch0to16 s
            d <-
                if length >= 8 then shiftMix (a + fetch64 s)
                else shiftMix (a + c)
        else
            c <- ch16 (fetch64 (rest.Slice(length - 8)) + k1) a
            d <- ch16 (b + uint64 length) (c + fetch64 (rest.Slice(length - 16)))
            a <- a + d
            // Initial block (length > 16 guarantees >= 16 bytes available).
            a <- a ^^^ (shiftMix (fetch64 rest * k1) * k1)
            a <- a * k1
            b <- b ^^^ a
            c <- c ^^^ (shiftMix (fetch64 (rest.Slice(8)) * k1) * k1)
            c <- c * k1
            d <- d ^^^ c
            rest <- rest.Slice(16)
            l <- l - 16
            if l > 0 then
                let mutable cont = true
                while cont && rest.Length >= 16 do
                    a <- a ^^^ (shiftMix (fetch64 rest * k1) * k1)
                    a <- a * k1
                    b <- b ^^^ a
                    c <- c ^^^ (shiftMix (fetch64 (rest.Slice(8)) * k1) * k1)
                    c <- c * k1
                    d <- d ^^^ c
                    rest <- rest.Slice(16)
                    l <- l - 16
                    if l <= 0 then cont <- false
        a <- ch16 a c
        b <- ch16 d b
        { Low = a ^^^ b; High = ch16 b a }

    /// `ch_128.go: CH128Seed`. The main 128-bit hash with an explicit seed.
    let private ch128Seed (s: ReadOnlySpan<byte>) (seed: U128) : U128 =
        if s.Length < 128 then
            chMurmur s seed
        else
            let t = s
            let mutable rest = s
            let mutable x = seed.Low
            let mutable y = seed.High
            let mutable z = uint64 s.Length * k1

            // Initial 56 bytes of state (v, w).
            let vLow0 = rot64 (y ^^^ k1) 49 * k1 + fetch64 rest
            let mutable v = { Low = vLow0
                              High = rot64 vLow0 42 * k1 + fetch64 (rest.Slice(8)) }
            let mutable w = { Low = rot64 (y + z) 35 * k1 + x
                              High = rot64 (x + fetch64 (rest.Slice(88))) 53 * k1 }

            // Main loop: same as CH64()'s, manually unrolled to 128 bytes per iter.
            while rest.Length >= 128 do
                // Roll 1
                x <- rot64 (x + y + v.Low + fetch64 (rest.Slice(16))) 37 * k1
                y <- rot64 (y + v.High + fetch64 (rest.Slice(48))) 42 * k1
                x <- x ^^^ w.High
                y <- y ^^^ v.Low
                z <- rot64 (z ^^^ w.Low) 33
                v <- weakHash32SeedsByte rest (v.High * k1) (x + w.Low)
                w <- weakHash32SeedsByte (rest.Slice(32)) (z + w.High) y
                let tmp = z in z <- x; x <- tmp
                // Roll 2
                x <- rot64 (x + y + v.Low + fetch64 (rest.Slice(80))) 37 * k1
                y <- rot64 (y + v.High + fetch64 (rest.Slice(112))) 42 * k1
                x <- x ^^^ w.High
                y <- y ^^^ v.Low
                z <- rot64 (z ^^^ w.Low) 33
                v <- weakHash32SeedsByte (rest.Slice(64)) (v.High * k1) (x + w.Low)
                w <- weakHash32SeedsByte (rest.Slice(96)) (z + w.High) y
                let tmp = z in z <- x; x <- tmp
                rest <- rest.Slice(128)

            y <- y + rot64 w.Low 37 * k0 + z
            x <- x + rot64 (v.Low + z) 49 * k0

            // Tail: hash up to 4 chunks of 32 bytes from the end.
            let mutable i = 0
            while i < rest.Length do
                i <- i + 32
                y <- rot64 (y - x) 42 * k0 + v.High
                w <- { w with Low = w.Low + fetch64 (t.Slice(t.Length - i + 16)) }
                x <- rot64 x 49 * k0 + w.Low
                w <- { w with Low = w.Low + v.Low }
                v <- weakHash32SeedsByte (t.Slice(t.Length - i)) v.Low v.High

            x <- ch16 x v.Low
            y <- ch16 y w.Low

            { Low = ch16 (x + v.High) w.High + y
              High = ch16 (x + w.High) (y + v.High) }

    /// ClickHouse 128-bit CityHash.
    let hash (s: ReadOnlySpan<byte>) : U128 =
        let n = s.Length
        if n >= 16 then
            let seed = { Low = fetch64 s ^^^ k3; High = fetch64 (s.Slice(8)) }
            ch128Seed (s.Slice(16)) seed
        elif n >= 8 then
            let l = uint64 n
            let seed =
                { Low = fetch64 s ^^^ (l * k0)
                  High = fetch64 (s.Slice(n - 8)) ^^^ k1 }
            ch128Seed (ReadOnlySpan<byte>()) seed
        else
            ch128Seed s { Low = k0; High = k1 }
