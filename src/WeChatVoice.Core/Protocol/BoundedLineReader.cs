using System.Buffers;
using System.Text;

namespace WeChatVoice.Core.Protocol;

/// <summary>
/// Stateful bounded newline framing shared by the Broker and its CLI client.
/// A single underlying read may contain multiple messages; unread characters
/// remain in the instance for the next call.
/// </summary>
public sealed class BoundedLineReader : IDisposable
{
    private readonly TextReader _reader;
    private readonly int _maximumLength;
    private readonly char[] _buffer;
    private int _offset;
    private int _available;
    private bool _disposed;

    public BoundedLineReader(TextReader reader, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (maximumLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        }

        _reader = reader;
        _maximumLength = maximumLength;
        _buffer = ArrayPool<char>.Shared.Rent(Math.Min(4096, maximumLength + 1));
    }

    public async ValueTask<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var builder = new StringBuilder(Math.Min(_maximumLength, 4096));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_offset >= _available)
            {
                _offset = 0;
                _available = await _reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (_available == 0)
                {
                    if (builder.Length == 0)
                    {
                        return null;
                    }

                    throw new InvalidDataException("The protocol line ended before a newline delimiter.");
                }
            }

            while (_offset < _available)
            {
                var character = _buffer[_offset++];
                if (character == '\n')
                {
                    if (builder.Length > 0 && builder[^1] == '\r')
                    {
                        builder.Length--;
                    }

                    return builder.ToString();
                }

                if (builder.Length >= _maximumLength)
                {
                    throw new InvalidDataException("The protocol line exceeded its fixed limit.");
                }

                builder.Append(character);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ArrayPool<char>.Shared.Return(_buffer, clearArray: true);
    }
}
