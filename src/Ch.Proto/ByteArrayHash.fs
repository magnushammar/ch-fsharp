namespace Ch.Proto

open System
open System.Collections.Generic

/// Content-based equality for `byte[]` keys. Default array equality is
/// reference-based, which makes a `Dictionary<byte[], _>` useless for
/// deduping fixed-string content. This comparer hashes contents
/// (FNV-1a, 64-bit folded to int) and compares via
/// `MemoryExtensions.SequenceEqual` on the byte spans.
///
/// Used by `ColLowCardinality<byte[]>` (the `LowCardinality(FixedString(N))`
/// path) to dedup wire content. Singleton — comparer holds no state.
[<Sealed>]
type ByteArrayContentEqualityComparer private () =
    static let instance =
        ByteArrayContentEqualityComparer() :> IEqualityComparer<byte array>

    static member Instance : IEqualityComparer<byte array> = instance

    interface IEqualityComparer<byte array> with
        // .NET annotates IEqualityComparer<T>.Equals as nullable params
        // (`T?`) and GetHashCode as `[DisallowNull] T` (non-null).
        // Match on null first to satisfy F# strict Nullable.
        member _.Equals(a, b) =
            match a, b with
            | null, null -> true
            | null, _ | _, null -> false
            | a, b ->
                obj.ReferenceEquals(a, b)
                || (a.Length = b.Length
                    && MemoryExtensions.SequenceEqual(
                        ReadOnlySpan<byte>(a),
                        ReadOnlySpan<byte>(b)))

        member _.GetHashCode(a) =
            // FNV-1a 64-bit. Folded to int via xor of high/low halves —
            // .NET's GetHashCode contract requires int.
            let mutable h = 0xcbf29ce484222325UL
            for i in 0 .. a.Length - 1 do
                h <- (h ^^^ uint64 a.[i]) * 0x100000001b3UL
            int (h ^^^ (h >>> 32))
