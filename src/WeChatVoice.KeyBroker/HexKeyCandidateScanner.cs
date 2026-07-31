using System.Security.Cryptography;

namespace WeChatVoice.KeyBroker;

internal delegate bool HexKeyCandidateHandler(ReadOnlySpan<byte> key, ReadOnlySpan<byte> salt);

/// <summary>
/// Recognizes only WCDB-style ASCII x'&lt;64 hex&gt;' and
/// x'&lt;64 hex key&gt;&lt;32 hex salt&gt;' values. It never returns addresses or
/// retains decoded key bytes after the synchronous callback.
/// </summary>
internal sealed class HexKeyCandidateScanner : IDisposable
{
    internal const int MaximumCandidates = 4096;
    private const int MaximumPatternBytes = 2 + 96 + 1;
    private readonly byte[] tail = new byte[MaximumPatternBytes - 1];
    private readonly HexKeyCandidateHandler handler;
    private readonly int maximumCandidates;
    private readonly HashSet<string> candidateHashes = new(StringComparer.Ordinal);
    private int tailLength;

    internal HexKeyCandidateScanner(HexKeyCandidateHandler handler, int maximumCandidates = MaximumCandidates)
    {
        this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
        if (maximumCandidates is <= 0 or > MaximumCandidates)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCandidates));
        }

        this.maximumCandidates = maximumCandidates;
    }

    internal int CandidateCount { get; private set; }

    internal bool ProcessChunk(ReadOnlySpan<byte> chunk, bool startsRegion)
    {
        if (startsRegion)
        {
            CryptographicOperations.ZeroMemory(tail);
            tailLength = 0;
        }

        if (tailLength > 0)
        {
            Span<byte> boundary = stackalloc byte[(MaximumPatternBytes - 1) * 2];
            tail.AsSpan(0, tailLength).CopyTo(boundary);
            var prefixLength = Math.Min(chunk.Length, MaximumPatternBytes - 1);
            chunk[..prefixLength].CopyTo(boundary[tailLength..]);
            if (!Scan(boundary[..(tailLength + prefixLength)], tailLength))
            {
                CryptographicOperations.ZeroMemory(boundary);
                return false;
            }

            CryptographicOperations.ZeroMemory(boundary);
        }

        if (!Scan(chunk, chunk.Length))
        {
            return false;
        }

        tailLength = Math.Min(chunk.Length, tail.Length);
        chunk[^tailLength..].CopyTo(tail);
        return true;
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(tail);
        tailLength = 0;
        candidateHashes.Clear();
    }

    private bool Scan(ReadOnlySpan<byte> data, int maximumStartExclusive)
    {
        Span<byte> key = stackalloc byte[32];
        Span<byte> salt = stackalloc byte[16];
        Span<byte> candidateFingerprint = stackalloc byte[32];
        Span<byte> candidateMaterial = stackalloc byte[48];
        try
        {
            for (var start = 0; start < maximumStartExclusive && start + 67 <= data.Length; start++)
            {
                if (data[start] != (byte)'x' || data[start + 1] != (byte)'\'')
                {
                    continue;
                }

                var hex = data[(start + 2)..];
                var hexLength = 0;
                while (hexLength < 96 && hexLength < hex.Length && IsHex(hex[hexLength]))
                {
                    hexLength++;
                }

                if (hexLength is not (64 or 96) || hexLength >= hex.Length || hex[hexLength] != (byte)'\'')
                {
                    continue;
                }

                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(salt);
                DecodeHex(hex[..64], key);
                if (hexLength == 96)
                {
                    DecodeHex(hex.Slice(64, 32), salt);
                }

                key.CopyTo(candidateMaterial);
                if (hexLength == 96)
                {
                    salt.CopyTo(candidateMaterial[32..]);
                }

                SHA256.HashData(candidateMaterial[..(hexLength == 96 ? 48 : 32)], candidateFingerprint);
                var fingerprint = Convert.ToHexString(candidateFingerprint);
                CryptographicOperations.ZeroMemory(candidateMaterial);
                CryptographicOperations.ZeroMemory(candidateFingerprint);
                if (!candidateHashes.Add(fingerprint))
                {
                    continue;
                }

                CandidateCount++;
                if (CandidateCount > maximumCandidates)
                {
                    return false;
                }

                if (!handler(key, hexLength == 96 ? salt : ReadOnlySpan<byte>.Empty))
                {
                    return false;
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(candidateFingerprint);
            CryptographicOperations.ZeroMemory(candidateMaterial);
        }

        return true;
    }

    private static bool IsHex(byte value) =>
        value is >= (byte)'0' and <= (byte)'9' or
        >= (byte)'a' and <= (byte)'f' or
        >= (byte)'A' and <= (byte)'F';

    private static void DecodeHex(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        for (var index = 0; index < destination.Length; index++)
        {
            destination[index] = (byte)((Nibble(source[index * 2]) << 4) | Nibble(source[(index * 2) + 1]));
        }
    }

    private static int Nibble(byte value) => value <= (byte)'9'
        ? value - (byte)'0'
        : (value | 0x20) - (byte)'a' + 10;
}
