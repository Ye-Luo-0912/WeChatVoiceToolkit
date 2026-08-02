using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Materialization;
using WeChatVoice.Infrastructure.Snapshots;

namespace WeChatVoice.Infrastructure.Serialization;

/// <summary>
/// Builds a <see cref="VerifiedFileIndex"/> for a root in one enumeration
/// pass, computing length and SHA-256 for every regular file exactly once and
/// refusing reparse points.
/// </summary>
public static class FileIndexBuilder
{
    public static async Task<VerifiedFileIndex> BuildAsync(string root, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"The file index root was not found: '{fullRoot}'.");
        }

        if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"The file index root cannot be a reparse point: '{fullRoot}'.");
        }

        var entries = new Dictionary<string, VerifiedFileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in SnapshotFileCopier.EnumerateRegularFiles(fullRoot).Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(fullRoot, path).Replace('\\', '/');
            // The materialization lock is intentionally held open with a
            // byte-range lock during Recovery/Delete. It is control metadata,
            // not a workspace artifact, and reading it here would conflict
            // with the held range lock on Windows.
            if (MaterializationStateStore.IsLockPath(relative))
            {
                continue;
            }

            var metadata = await FileHashing.ComputeMetadataAsync(path, cancellationToken).ConfigureAwait(false);
            entries[relative] = new VerifiedFileEntry(
                metadata.ByteLength,
                metadata.Sha256,
                File.GetLastWriteTimeUtc(path),
                metadata.HasPlainSqliteHeader);
        }

        return new VerifiedFileIndex(entries);
    }
}
