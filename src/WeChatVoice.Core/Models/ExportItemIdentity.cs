using System.Security.Cryptography;
using System.Text;

namespace WeChatVoice.Core.Models;

/// <summary>
/// Stable, non-reversible identity used by portable dataset manifests and
/// curation profiles.  It is deliberately independent of local paths.
/// </summary>
public static class ExportItemIdentity
{
    public static string ComputeItemId(VoiceExportEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var sourceIdentity = entry.SourceStableKey ?? $"{entry.MessageId}\n{entry.OriginalSha256}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceIdentity))).ToLowerInvariant();
    }
}
