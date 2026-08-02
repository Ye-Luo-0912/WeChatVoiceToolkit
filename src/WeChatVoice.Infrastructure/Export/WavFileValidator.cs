using System.Buffers.Binary;

namespace WeChatVoice.Infrastructure.Export;

internal static class WavFileValidator
{
    internal static async Task<long?> TryReadDurationMsAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length < 12) return null;
            var header = new byte[12];
            if (await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false) != header.Length
                || !header.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8)) return null;
            var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
            if (riffSize + 8UL > (ulong)stream.Length) return null;

            uint sampleRate = 0;
            ushort blockAlign = 0;
            ushort bitsPerSample = 0;
            ushort channels = 0;
            ulong dataBytes = 0;
            var hasPcm = false;
            var chunkHeader = new byte[8];
            while (stream.Position + 8 <= stream.Length)
            {
                if (await ReadExactlyAsync(stream, chunkHeader, cancellationToken).ConfigureAwait(false) != chunkHeader.Length) return null;
                var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(4, 4));
                if (chunkSize > stream.Length - stream.Position) return null;
                if (chunkHeader.AsSpan(0, 4).SequenceEqual("fmt "u8))
                {
                    if (chunkSize < 16 || chunkSize > 1024 * 1024) return null;
                    var format = new byte[chunkSize];
                    if (await ReadExactlyAsync(stream, format, cancellationToken).ConfigureAwait(false) != format.Length) return null;
                    var audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(0, 2));
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2, 2));
                    sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4, 4));
                    blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(12, 2));
                    bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(14, 2));
                    hasPcm = audioFormat == 1;
                }
                else if (chunkHeader.AsSpan(0, 4).SequenceEqual("data"u8))
                {
                    dataBytes = checked(dataBytes + chunkSize);
                    stream.Position += chunkSize;
                }
                else stream.Position += chunkSize;
                if ((chunkSize & 1) != 0 && stream.Position < stream.Length) stream.Position++;
            }

            if (!hasPcm || channels == 0 || sampleRate == 0 || blockAlign == 0 || bitsPerSample is not (8 or 16 or 24 or 32) || dataBytes == 0)
                return null;
            var frames = dataBytes / blockAlign;
            return checked((long)((frames * 1000UL) / sampleRate));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (OverflowException) { return null; }
    }

    private static async Task<int> ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

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
