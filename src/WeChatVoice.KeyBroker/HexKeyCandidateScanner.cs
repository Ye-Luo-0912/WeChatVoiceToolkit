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
    private int tailLength;

    internal HexKeyCandidateScanner(HexKeyCandidateHandler handler)
    {
        this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
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
    }

    private bool Scan(ReadOnlySpan<byte> data, int maximumStartExclusive)
    {
        Span<byte> key = stackalloc byte[32];
        Span<byte> salt = stackalloc byte[16];
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

                CandidateCount++;
                if (CandidateCount > MaximumCandidates)
                {
                    return false;
                }

                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(salt);
                DecodeHex(hex[..64], key);
                if (hexLength == 96)
                {
                    DecodeHex(hex.Slice(64, 32), salt);
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
