using System.Security.Cryptography;

namespace WeChatVoice.Infrastructure.Snapshots;

internal sealed class SnapshotFileCopier
{
    private const int BufferSize = 128 * 1024;

    internal async Task<SnapshotCopiedFile> CopyStableFileAsync(
        string sourcePath,
        string destinationPath,
        bool requireStableSource,
        CancellationToken cancellationToken)
    {
        var before = CaptureState(sourcePath);
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException("The destination path must include a directory.", nameof(destinationPath));
        Directory.CreateDirectory(destinationDirectory);

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long copiedByteLength = 0;
        await using (var source = OpenRead(sourcePath))
        await using (var destination = OpenWrite(destinationPath))
        {
            var buffer = new byte[BufferSize];
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
            {
                hasher.AppendData(buffer, 0, bytesRead);
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                copiedByteLength += bytesRead;
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        var after = CaptureState(sourcePath);
        if (requireStableSource && before != after)
        {
            throw new SnapshotSourceChangedException(sourcePath, before, after);
        }

        // A snapshot is a copy of the source at a point in time, so retain the
        // timestamp used by the stability check instead of assigning copy time.
        File.SetLastWriteTimeUtc(destinationPath, before.LastWriteTimeUtc);

        return new SnapshotCopiedFile(
            copiedByteLength,
            before.LastWriteTimeUtc,
            Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant());
    }

    internal static IEnumerable<string> EnumerateRegularFiles(string sourceRoot)
    {
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(sourceRoot);

        while (pendingDirectories.Count > 0)
        {
            var directory = pendingDirectories.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).OrderBy(static path => path, StringComparer.Ordinal))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // Reparse points can escape the user-selected source tree or
                    // create cycles. A snapshot copies regular source files only.
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pendingDirectories.Push(entry);
                }
                else
                {
                    yield return entry;
                }
            }
        }
    }

    private static SnapshotFileState CaptureState(string path)
    {
        var info = new FileInfo(path);
        info.Refresh();

        if (!info.Exists)
        {
            throw new FileNotFoundException("The source file no longer exists.", path);
        }

        return new SnapshotFileState(info.Length, info.LastWriteTimeUtc);
    }

    private static FileStream OpenRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        BufferSize,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static FileStream OpenWrite(string path) => new(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        BufferSize,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
}

internal readonly record struct SnapshotFileState(long Length, DateTime LastWriteTimeUtc);

internal sealed record SnapshotCopiedFile(long ByteLength, DateTime LastWriteTimeUtc, string Sha256);

/// <summary>
/// Indicates that size or last-write time changed while a source file was copied.
/// </summary>
public sealed class SnapshotSourceChangedException : IOException
{
    internal SnapshotSourceChangedException(string sourcePath, SnapshotFileState before, SnapshotFileState after)
        : base($"Snapshot source changed while it was being copied: '{sourcePath}'. " +
               $"Before: {before.Length} bytes at {before.LastWriteTimeUtc:O}; " +
               $"after: {after.Length} bytes at {after.LastWriteTimeUtc:O}.")
    {
        SourcePath = sourcePath;
    }

    public string SourcePath { get; }
}
