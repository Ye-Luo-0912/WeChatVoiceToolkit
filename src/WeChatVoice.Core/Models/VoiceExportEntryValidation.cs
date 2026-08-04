namespace WeChatVoice.Core.Models;

/// <summary>
/// Shared validation for entries crossing from an export archive into a
/// derived dataset.  Curation and Dataset Build must agree on the same
/// artifact identity rules; otherwise the UI could offer an item that Build
/// can never safely copy.
/// </summary>
public static class VoiceExportEntryValidation
{
    public static bool HasValidOriginalArtifact(VoiceExportEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return !string.IsNullOrWhiteSpace(entry.SourceStableKey)
            && !string.IsNullOrWhiteSpace(entry.OriginalPath)
            && !Path.IsPathRooted(entry.OriginalPath)
            && entry.OriginalByteLength > 0
            && IsSha256(entry.OriginalSha256);
    }

    public static bool IsSha256(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length == 64
            && value.All(Uri.IsHexDigit);
}
