using System.Buffers;
using System.Text;

namespace WeChatVoice.Core.Protocol;

/// <summary>
/// Reads one newline-delimited protocol message without ever buffering more
/// than the configured maximum. It is shared by the unprivileged client and
/// the elevated broker so both sides enforce the same framing contract.
/// </summary>
public static class BoundedLineReader
{
    public static async ValueTask<string?> ReadAsync(
        TextReader reader,
        int maximumLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (maximumLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        }

        var buffer = ArrayPool<char>.Shared.Rent(Math.Min(4096, maximumLength + 1));
        var builder = new StringBuilder(Math.Min(maximumLength, 4096));
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    if (builder.Length == 0)
                    {
                        return null;
                    }

                    throw new InvalidDataException("The protocol line ended before a newline delimiter.");
                }

                var newline = buffer.AsSpan(0, read).IndexOf('\n');
                var count = newline >= 0 ? newline : read;
                if (builder.Length + count > maximumLength)
                {
                    throw new InvalidDataException("The protocol line exceeded its fixed limit.");
                }

                if (count > 0)
                {
                    builder.Append(buffer, 0, count);
                }

                if (newline >= 0)
                {
                    if (builder.Length > 0 && builder[^1] == '\r')
                    {
                        builder.Length--;
                    }

                    return builder.ToString();
                }
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer, clearArray: true);
        }
    }
}
