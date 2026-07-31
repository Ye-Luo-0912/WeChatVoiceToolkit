using System.Security.Cryptography;

namespace WeChatVoice.Infrastructure.Serialization;

internal static class FileHashing
{
    private const int BufferSize = 128 * 1024;

    internal static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await ComputeSha256Async(stream, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<string> ComputeSha256Async(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            hasher.AppendData(buffer, 0, bytesRead);
        }

        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    internal static async Task<FileHashMetadata> ComputeMetadataAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var header = new byte[16];
        var headerRead = 0;
        var buffer = new byte[BufferSize];
        long length = 0;
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            hasher.AppendData(buffer, 0, bytesRead);
            if (headerRead < header.Length)
            {
                var copy = Math.Min(bytesRead, header.Length - headerRead);
                buffer.AsSpan(0, copy).CopyTo(header.AsSpan(headerRead));
                headerRead += copy;
            }

            length = checked(length + bytesRead);
        }

        return new FileHashMetadata(
            length,
            Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant(),
            headerRead == header.Length && header.AsSpan().SequenceEqual("SQLite format 3\0"u8));
    }
}

internal sealed record FileHashMetadata(long ByteLength, string Sha256, bool HasPlainSqliteHeader);
