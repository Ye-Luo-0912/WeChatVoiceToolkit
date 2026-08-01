using System.Buffers.Binary;

namespace WeChatVoice.Infrastructure.Export;

internal static class WavFileValidator
{
    internal static async Task<bool> IsValidAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length < 12)
            {
                return false;
            }

            var header = new byte[12];
            if (await stream.ReadAsync(header.AsMemory(0, header.Length), cancellationToken).ConfigureAwait(false) != header.Length
                || !header.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            {
                return false;
            }

            var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
            if (riffSize + 8UL > (ulong)stream.Length)
            {
                return false;
            }

            var hasPcmFormat = false;
            var hasData = false;
            var chunkHeader = new byte[8];
            while (stream.Position + 8 <= stream.Length)
            {
                if (await stream.ReadAsync(chunkHeader.AsMemory(0, chunkHeader.Length), cancellationToken).ConfigureAwait(false) != chunkHeader.Length)
                {
                    return false;
                }

                var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(4, 4));
                if (chunkSize > stream.Length - stream.Position)
                {
                    return false;
                }

                if (chunkHeader.AsSpan(0, 4).SequenceEqual("fmt "u8))
                {
                    if (chunkSize < 16 || chunkSize > 1024 * 1024)
                    {
                        return false;
                    }

                    var format = new byte[checked((int)chunkSize)];
                    if (await stream.ReadAsync(format.AsMemory(0, format.Length), cancellationToken).ConfigureAwait(false) != format.Length)
                    {
                        return false;
                    }

                    var audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(0, 2));
                    var channels = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2, 2));
                    var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4, 4));
                    var blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(12, 2));
                    var bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(14, 2));
                    hasPcmFormat = audioFormat == 1
                        && channels > 0
                        && sampleRate > 0
                        && bitsPerSample is 8 or 16 or 24 or 32
                        && blockAlign > 0;
                }
                else if (chunkHeader.AsSpan(0, 4).SequenceEqual("data"u8))
                {
                    hasData = chunkSize > 0;
                    stream.Position += chunkSize;
                }
                else
                {
                    stream.Position += chunkSize;
                }

                if ((chunkSize & 1) != 0 && stream.Position < stream.Length)
                {
                    stream.Position++;
                }
            }

            return hasPcmFormat && hasData;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
