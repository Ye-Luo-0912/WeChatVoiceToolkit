using System.Security.Cryptography;

namespace WeChatVoice.KeyBroker;

internal delegate bool HexKeyCandidateHandler(ReadOnlySpan<byte> key, ReadOnlySpan<byte> salt);

/// <summary>
/// Recognizes WCDB-style x'&lt;64 hex&gt;' and x'&lt;64 hex key&gt;&lt;32 hex salt&gt;'
/// values, plus longer even-length WCDB key-spec strings where the first 64
/// hex characters are the key and the final 32 are the page salt. A
/// version-specific repeating XOR mask can be supplied for builds that keep
/// the spec protected in memory. It never returns addresses or retains
/// decoded key bytes after the synchronous callback.
/// </summary>
internal sealed class HexKeyCandidateScanner : IDisposable
{
    internal const int MaximumCandidates = 4096;
    private const int MaximumHexCharacters = 192;
    private const int MaximumPatternBytes = 2 + MaximumHexCharacters + 1;
    private readonly byte[] tail = new byte[MaximumPatternBytes - 1];
    private readonly HexKeyCandidateHandler handler;
    private readonly int maximumCandidates;
    private readonly byte[] protectedSpecXorMask;
    private readonly HashSet<string> candidateHashes = new(StringComparer.Ordinal);
    private int tailLength;

    internal HexKeyCandidateScanner(HexKeyCandidateHandler handler, int maximumCandidates = MaximumCandidates)
        : this(handler, ReadOnlySpan<byte>.Empty, maximumCandidates)
    {
    }

    internal HexKeyCandidateScanner(
        HexKeyCandidateHandler handler,
        ReadOnlySpan<byte> protectedSpecXorMask,
        int maximumCandidates = MaximumCandidates)
    {
        this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
        if (maximumCandidates is <= 0 or > MaximumCandidates)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCandidates));
        }

        this.maximumCandidates = maximumCandidates;
        if (protectedSpecXorMask.Length is not (0 or 32))
        {
            throw new ArgumentException("The protected WCDB spec mask must contain exactly 32 bytes.", nameof(protectedSpecXorMask));
        }

        this.protectedSpecXorMask = protectedSpecXorMask.ToArray();
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
            if (!ScanAllRepresentations(boundary[..(tailLength + prefixLength)], tailLength))
            {
                CryptographicOperations.ZeroMemory(boundary);
                return false;
            }

            CryptographicOperations.ZeroMemory(boundary);
        }

        if (!ScanAllRepresentations(chunk, chunk.Length))
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
        CryptographicOperations.ZeroMemory(protectedSpecXorMask);
        tailLength = 0;
        candidateHashes.Clear();
    }

    private bool ScanAllRepresentations(ReadOnlySpan<byte> data, int maximumStartExclusive)
    {
        if (!Scan(data, maximumStartExclusive, ReadOnlySpan<byte>.Empty))
        {
            return false;
        }

        return protectedSpecXorMask.Length == 0 || Scan(data, maximumStartExclusive, protectedSpecXorMask);
    }

    private bool Scan(ReadOnlySpan<byte> data, int maximumStartExclusive, ReadOnlySpan<byte> xorMask)
    {
        Span<byte> key = stackalloc byte[32];
        Span<byte> salt = stackalloc byte[16];
        Span<byte> candidateFingerprint = stackalloc byte[32];
        Span<byte> candidateMaterial = stackalloc byte[48];
        try
        {
            for (var start = 0; start < maximumStartExclusive && start + 67 <= data.Length; start++)
            {
                if (Transform(data[start], 0, xorMask) != (byte)'x'
                    || Transform(data[start + 1], 1, xorMask) != (byte)'\'')
                {
                    continue;
                }

                var hex = data[(start + 2)..];
                var hexLength = 0;
                while (hexLength < MaximumHexCharacters
                    && hexLength < hex.Length
                    && IsHex(Transform(hex[hexLength], hexLength + 2, xorMask)))
                {
                    hexLength++;
                }

                var hasRecognizedLength = hexLength == 64
                    || hexLength == 96
                    || hexLength > 96 && (hexLength & 1) == 0;
                if (!hasRecognizedLength
                    || hexLength >= hex.Length
                    || Transform(hex[hexLength], hexLength + 2, xorMask) != (byte)'\'')
                {
                    continue;
                }

                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(salt);
                DecodeHex(hex[..64], key, xorMask, 2);
                if (hexLength >= 96)
                {
                    DecodeHex(hex.Slice(hexLength - 32, 32), salt, xorMask, hexLength - 30);
                }

                key.CopyTo(candidateMaterial);
                if (hexLength >= 96)
                {
                    salt.CopyTo(candidateMaterial[32..]);
                }

                SHA256.HashData(candidateMaterial[..(hexLength >= 96 ? 48 : 32)], candidateFingerprint);
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

                if (!handler(key, hexLength >= 96 ? salt : ReadOnlySpan<byte>.Empty))
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

    private static byte Transform(byte value, int offset, ReadOnlySpan<byte> xorMask) =>
        xorMask.Length == 0 ? value : (byte)(value ^ xorMask[offset & 31]);

    private static void DecodeHex(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        ReadOnlySpan<byte> xorMask,
        int sourceOffset)
    {
        for (var index = 0; index < destination.Length; index++)
        {
            var highOffset = sourceOffset + (index * 2);
            destination[index] = (byte)((Nibble(Transform(source[index * 2], highOffset, xorMask)) << 4)
                | Nibble(Transform(source[(index * 2) + 1], highOffset + 1, xorMask)));
        }
    }

    private static int Nibble(byte value) => value <= (byte)'9'
        ? value - (byte)'0'
        : (value | 0x20) - (byte)'a' + 10;
}
