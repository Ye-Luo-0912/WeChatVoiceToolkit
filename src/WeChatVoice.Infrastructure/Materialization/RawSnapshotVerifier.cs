using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Infrastructure.Materialization;

public sealed class RawSnapshotVerifier : IRawSnapshotVerifier
{
    public async Task<VerifiedRawSnapshot> VerifyAsync(RawSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(snapshot.SnapshotDirectory);
        if (!Directory.Exists(root))
        {
            throw new RawSnapshotVerificationException($"The raw snapshot directory was not found: '{root}'.");
        }

        var expected = snapshot.Manifest.Files.ToDictionary(static file => file.RelativePath.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
        if (expected.Count == 0)
        {
            throw new RawSnapshotVerificationException("The raw snapshot manifest contains no files to verify.");
        }

        var actual = EnumerateRegularFilesStrict(root)
            .Where(path => !IsInternalMetadataPath(root, path))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actual.SetEquals(expected.Keys))
        {
            throw new RawSnapshotVerificationException("The raw snapshot file set differs from its manifest.");
        }

        foreach (var pair in expected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = CombineUnderRoot(root, pair.Key);
            var info = new FileInfo(path);
            var hash = await FileHashing.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
            if (info.Length != pair.Value.ByteLength
                || !string.Equals(hash, pair.Value.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new RawSnapshotVerificationException($"The raw snapshot file failed manifest verification: '{pair.Key}'.");
            }
        }

        return new VerifiedRawSnapshot(snapshot, DateTimeOffset.UtcNow);
    }

    private static IEnumerable<string> EnumerateRegularFilesStrict(string root)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new RawSnapshotVerificationException($"A raw snapshot root cannot be a reparse point: '{root}'.");
        }

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new RawSnapshotVerificationException($"A raw snapshot contains a reparse point: '{entry}'.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else
                {
                    yield return entry;
                }
            }
        }
    }

    private static bool IsInternalMetadataPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return relative.Equals(".wechatvoice", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith(".wechatvoice/", StringComparison.OrdinalIgnoreCase);
    }

    private static string CombineUnderRoot(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new RawSnapshotVerificationException("The raw snapshot manifest contains an absolute path.");
        }

        var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new RawSnapshotVerificationException("The raw snapshot manifest contains a path outside its root.");
        }

        return candidate;
    }
}

public sealed class RawSnapshotVerificationException : IOException
{
    public RawSnapshotVerificationException(string message)
        : base(message)
    {
    }
}
