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

    /// <summary>
    /// Computes a dataset-scoped opaque ID. A namespace key prevents the same
    /// source record from being linkable across independent exports while the
    /// private manifest retains the mapping needed for repeatable builds.
    /// </summary>
    public static string ComputeItemId(VoiceExportEntry entry, string? datasetNamespaceKey)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(datasetNamespaceKey))
        {
            return ComputeItemId(entry);
        }

        byte[] key;
        try
        {
            key = Convert.FromHexString(datasetNamespaceKey);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The dataset namespace key must be hexadecimal.", nameof(datasetNamespaceKey), exception);
        }

        if (key.Length < 16)
        {
            throw new ArgumentException("The dataset namespace key is too short.", nameof(datasetNamespaceKey));
        }

        var sourceIdentity = entry.SourceStableKey ?? $"{entry.MessageId}\n{entry.OriginalSha256}";
        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(sourceIdentity))).ToLowerInvariant();
    }

    public static VoiceDatasetManifest ToPortableManifest(VoiceExportManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(manifest.DatasetNamespaceKey))
        {
            throw new InvalidDataException("A portable export manifest requires a dataset namespace key.");
        }

        if (manifest.Entries.Any(static entry => string.IsNullOrWhiteSpace(entry.SourceStableKey)))
        {
            throw new InvalidDataException("A portable export manifest requires a complete SourceStableKey for every entry.");
        }

        return new VoiceDatasetManifest(
            manifest.GeneratedAtUtc,
            manifest.RunId,
            manifest.Entries.Select(entry => new VoiceDatasetEntry(
                ComputeItemId(entry, manifest.DatasetNamespaceKey),
                entry.OriginalPath,
                entry.OriginalSha256,
                entry.OriginalByteLength,
                entry.DurationMs,
                entry.QualityFlags,
                entry.TrainingEligibility,
                entry.UserSelectionState == UserSelectionState.Selected)).ToArray());
    }
}
