using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WeChatVoice.Core.Models;

/// <summary>
/// Single authoritative computation of the database-group fingerprint. Every
/// consumer — dataset probing, workspace verification, and the Broker's
/// database-group lease — derives the same canonical string so a fingerprint
/// always means the same bytes. Sidecar values are empty when absent.
/// </summary>
public static class DatabaseGroupFingerprint
{
    public static string Compute(
        string relativePath,
        string logicalRole,
        int? shardNumber,
        long mainLength,
        string mainHash,
        long? walLength,
        string? walHash,
        long? shmLength,
        string? shmHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalRole);
        ArgumentException.ThrowIfNullOrWhiteSpace(mainHash);

        var canonical = string.Join('|',
            relativePath.Replace('\\', '/'),
            logicalRole,
            shardNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            mainLength.ToString(CultureInfo.InvariantCulture),
            mainHash.ToLowerInvariant(),
            walLength?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            walHash?.ToLowerInvariant() ?? string.Empty,
            shmLength?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            shmHash?.ToLowerInvariant() ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
