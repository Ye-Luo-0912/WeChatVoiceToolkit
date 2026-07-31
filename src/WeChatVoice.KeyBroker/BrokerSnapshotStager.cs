using System.Buffers;
using System.Security.Cryptography;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Snapshots;

namespace WeChatVoice.KeyBroker;

/// <summary>
/// Makes an operation-private, content-verified copy of the raw input before
/// any profile or worker opens a database. Source handles are held open while
/// each file is copied so a replace/delete race fails closed.
/// </summary>
internal static class BrokerSnapshotStager
{
    internal static async Task<BrokerStagedSnapshot> StageAsync(
        VerifiedRawSnapshot verifiedSnapshot,
        string stagingParent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verifiedSnapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingParent);
        if (verifiedSnapshot.Snapshot.Manifest.PotentiallyInconsistent)
        {
            throw new InvalidDataException("Potentially inconsistent live-source snapshots cannot be materialized by the Key Broker.");
        }

        var parent = Path.GetFullPath(stagingParent);
        Directory.CreateDirectory(parent);
        if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The Broker snapshot staging parent cannot be a reparse point.");
        }

        var staging = Path.Combine(parent, $".wechatvoice-broker-snapshot-{Guid.NewGuid():N}");
        if (Directory.Exists(staging) || File.Exists(staging))
        {
            throw new IOException("The Broker snapshot staging path already exists.");
        }

        Directory.CreateDirectory(staging);
        try
        {
            var sourceRoot = Path.GetFullPath(verifiedSnapshot.Snapshot.SnapshotDirectory);
            foreach (var file in verifiedSnapshot.Snapshot.Manifest.Files.OrderBy(static item => item.RelativePath, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = NormalizeRelative(file.RelativePath);
                var sourcePath = CombineUnderRoot(sourceRoot, relative);
                var destinationPath = CombineUnderRoot(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                await CopyAndVerifyAsync(sourcePath, destinationPath, file, cancellationToken).ConfigureAwait(false);
            }

            var raw = new RawSnapshot(verifiedSnapshot.Snapshot.Manifest, staging);
            return new BrokerStagedSnapshot(new VerifiedRawSnapshot(raw, DateTimeOffset.UtcNow), staging);
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    private static async Task CopyAndVerifyAsync(
        string sourcePath,
        string destinationPath,
        SnapshotFileRecord expected,
        CancellationToken cancellationToken)
    {
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long copied = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                copied += read;
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (copied != expected.ByteLength
                || source.Length != expected.ByteLength
                || !string.Equals(actualHash, expected.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The snapshot source changed while staging '{expected.RelativePath}'.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }

    private static string NormalizeRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            throw new InvalidDataException("A snapshot file path was not relative.");
        }

        var normalized = SnapshotFileCopier.NormalizeRelativePath(path);
        if (normalized.Equals(".", StringComparison.Ordinal) || normalized.StartsWith("../", StringComparison.Ordinal) || normalized.Contains("/../", StringComparison.Ordinal))
        {
            throw new InvalidDataException("A snapshot file path escaped its root.");
        }

        return normalized;
    }

    private static string CombineUnderRoot(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A snapshot file path escaped its root.");
        }

        return candidate;
    }

    private static void TryDelete(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        Directory.Delete(path, recursive: true);
    }
}

internal sealed class BrokerStagedSnapshot(VerifiedRawSnapshot snapshot, string stagingDirectory) : IAsyncDisposable
{
    public VerifiedRawSnapshot Snapshot { get; } = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        if (Directory.Exists(stagingDirectory))
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }
    }
}
