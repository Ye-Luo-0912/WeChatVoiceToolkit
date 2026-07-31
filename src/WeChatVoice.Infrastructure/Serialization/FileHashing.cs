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
}
