using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace WeChatVoice.Infrastructure.Snapshots;

internal sealed partial class SnapshotFileCopier
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

        File.SetLastWriteTimeUtc(destinationPath, before.LastWriteTimeUtc);

        return new SnapshotCopiedFile(
            copiedByteLength,
            before.LastWriteTimeUtc,
            Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant(),
            before.FileId);
    }

    internal static SnapshotSourceInventory CaptureInventory(
        string sourceRoot,
        Func<string, bool>? includeRelativePath = null)
    {
        var files = new SortedDictionary<string, SnapshotFileState>(StringComparer.Ordinal);
        foreach (var sourcePath in EnumerateRegularFiles(sourceRoot))
        {
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(sourceRoot, sourcePath));
            if (includeRelativePath is not null && !includeRelativePath(relativePath))
            {
                continue;
            }

            files.Add(relativePath, CaptureState(sourcePath));
        }

        return new SnapshotSourceInventory(files);
    }

    internal static IEnumerable<string> EnumerateRegularFiles(string sourceRoot)
    {
        if ((File.GetAttributes(sourceRoot) & FileAttributes.ReparsePoint) != 0)
        {
            yield break;
        }

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

    internal static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    internal static SnapshotFileState CaptureState(string path)
    {
        var info = new FileInfo(path);
        info.Refresh();

        if (!info.Exists)
        {
            throw new FileNotFoundException("The source file no longer exists.", path);
        }

        return new SnapshotFileState(info.Length, info.LastWriteTimeUtc, CaptureFileId(path));
    }

    private static string CaptureFileId(string path)
    {
        var info = new FileInfo(path);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var handle = File.OpenHandle(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    FileOptions.Asynchronous);
                if (GetFileInformationByHandle(handle, out var nativeInfo))
                {
                    return $"win:{nativeInfo.VolumeSerialNumber:X8}:{nativeInfo.FileIndexHigh:X8}{nativeInfo.FileIndexLow:X8}";
                }
            }
            catch (IOException)
            {
                // Fall through to a conservative fallback identity.
            }
            catch (UnauthorizedAccessException)
            {
                // Fall through to a conservative fallback identity.
            }
        }

        return $"fallback:{info.CreationTimeUtc.Ticks}:{info.FullName}";
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(
        SafeFileHandle handle,
        out ByHandleFileInformation fileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}

internal readonly record struct SnapshotFileState(long Length, DateTime LastWriteTimeUtc, string FileId);

internal sealed record SnapshotSourceInventory(IReadOnlyDictionary<string, SnapshotFileState> Files)
{
    internal bool IsEquivalentTo(SnapshotSourceInventory other)
    {
        if (Files.Count != other.Files.Count)
        {
            return false;
        }

        foreach (var pair in Files)
        {
            if (!other.Files.TryGetValue(pair.Key, out var otherState) || pair.Value != otherState)
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed record SnapshotCopiedFile(long Length, DateTime LastWriteTimeUtc, string Sha256, string FileId);

/// <summary>
/// Indicates that size, modification time, identity, or the file set changed
/// while the source group was being copied.
/// </summary>
public sealed class SnapshotSourceChangedException : IOException
{
    internal SnapshotSourceChangedException(string sourcePath, SnapshotFileState before, SnapshotFileState after)
        : base($"Snapshot source changed while it was being copied: '{sourcePath}'. " +
               $"Before: {before.Length} bytes at {before.LastWriteTimeUtc:O} ({before.FileId}); " +
               $"after: {after.Length} bytes at {after.LastWriteTimeUtc:O} ({after.FileId}).")
    {
        SourcePath = sourcePath;
    }

    internal SnapshotSourceChangedException(string message)
        : base(message)
    {
        SourcePath = string.Empty;
    }

    public string SourcePath { get; }
}
