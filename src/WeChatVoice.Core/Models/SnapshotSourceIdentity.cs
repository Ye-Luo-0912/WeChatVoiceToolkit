namespace WeChatVoice.Core.Models;

/// <summary>
/// Provenance describing how a snapshot's account identity was derived, kept
/// alongside the account candidate so a host never silently picks an account
/// from a path alone.
/// </summary>
public sealed record SnapshotSourceIdentity(
    string? AccountDirectoryName,
    string? AccountCandidate,
    string? SourceRootFileId,
    int SourceLayoutVersion)
{
    /// <summary>
    /// Derives identity from the fixed Weixin storage layout
    /// <c>&lt;account-dir&gt;\db_storage</c> where the account directory is
    /// <c>wxid_&lt;name&gt;_&lt;16 hex&gt;</c>. The candidate is the prefix;
    /// <see cref="SourceRootFileId"/> anchors the layout to a stable file
    /// identity inside <c>db_storage</c> from the verified manifest.
    /// </summary>
    public static SnapshotSourceIdentity? TryDerive(string sourceDirectory, IReadOnlyList<SnapshotFileRecord> files)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            return null;
        }

        var storage = new DirectoryInfo(Path.GetFullPath(sourceDirectory));
        if (!string.Equals(storage.Name, "db_storage", StringComparison.OrdinalIgnoreCase)
            || storage.Parent is not { } accountDirectory)
        {
            return null;
        }

        var name = accountDirectory.Name;
        var separator = name.LastIndexOf('_');
        if (separator <= "wxid_".Length
            || separator + 5 != name.Length
            || !name.StartsWith("wxid_", StringComparison.Ordinal)
            || !ContainsOnlyHexDigits(name.AsSpan(separator + 1)))
        {
            return null;
        }

        var candidate = name[..separator];
        // Snapshot files are relative to the db_storage root, so the first
        // manifest file with a stable FileId anchors the account layout.
        var anchor = files
            .OrderBy(static file => file.RelativePath, StringComparer.Ordinal)
            .Select(static file => file.FileId)
            .FirstOrDefault(static fileId => !string.IsNullOrWhiteSpace(fileId));
        return new SnapshotSourceIdentity(
            name,
            candidate,
            anchor,
            SourceLayoutVersion: 1);
    }

    private static bool ContainsOnlyHexDigits(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
