using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WeChatVoice.Core.Models;

/// <summary>
/// Computes the deterministic identity of one streamed voice result set. The
/// query parameters identify what was requested; this identity records what
/// the verified catalog actually returned.
/// </summary>
public sealed class VoiceResultSetFingerprintBuilder : IDisposable
{
    private IncrementalHash? _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool _completed;

    public int Count { get; private set; }

    public long TotalPayloadBytes { get; private set; }

    public void Append(VoiceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ObjectDisposedException.ThrowIf(_hash is null, this);
        if (_completed)
        {
            throw new InvalidOperationException("The result-set fingerprint is already complete.");
        }

        Count++;
        if (record.PayloadState == VoicePayloadState.Linked && record.PayloadByteLength is > 0)
        {
            TotalPayloadBytes = checked(TotalPayloadBytes + record.PayloadByteLength.Value);
        }

        var stableKey = record.SourceStableKey
            ?? throw new InvalidDataException("A result-set fingerprint requires a complete SourceStableKey.");
        var canonical = string.Join(
            '\n',
            stableKey,
            record.PayloadState,
            record.PayloadByteLength?.ToString(CultureInfo.InvariantCulture) ?? string.Empty) + '\n';
        _hash.AppendData(Encoding.UTF8.GetBytes(canonical));
    }

    public string Complete()
    {
        ObjectDisposedException.ThrowIf(_hash is null, this);
        if (_completed)
        {
            throw new InvalidOperationException("The result-set fingerprint is already complete.");
        }

        _completed = true;
        return Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
    }

    public void Dispose()
    {
        _hash?.Dispose();
        _hash = null;
    }
}
