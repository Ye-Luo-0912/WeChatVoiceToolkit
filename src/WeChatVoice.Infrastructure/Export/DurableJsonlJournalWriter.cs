using System.Text;
using System.Text.Json;

namespace WeChatVoice.Infrastructure.Serialization;

/// <summary>
/// The single append path for durable JSONL recovery data. A crash may leave a
/// final UTF-8 JSON object without its newline; before appending, that tail is
/// either completed with a newline or truncated to the last complete record.
/// A malformed line in the middle is never hidden by this writer.
/// </summary>
internal static class DurableJsonlJournalWriter
{
    private const int BufferSize = 64 * 1024;
    private static readonly byte[] Newline = [0x0A];

    public static async Task AppendAsync<T>(
        string path,
        IEnumerable<T> values,
        JsonSerializerOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(options);

        await AppendRawJsonLinesAsync(
            path,
            values.Select(value => JsonSerializer.Serialize(value, options)),
            options,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Appends already serialized JSON objects. Cache stores use this entry
    /// point so they share exactly the same torn-tail recovery and durable
    /// flush behavior as export Journals.
    /// </summary>
    internal static async Task AppendRawJsonLinesAsync(
        string path,
        IEnumerable<string> lines,
        JsonSerializerOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(options);

        await using var stream = new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await PrepareForAppendAsync(stream, options, cancellationToken).ConfigureAwait(false);
        stream.Position = stream.Length;

        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(line);
            var bytes = Encoding.UTF8.GetBytes(line);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(Newline, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task PrepareForAppendAsync(
        FileStream stream,
        JsonSerializerOptions options,
        CancellationToken cancellationToken)
    {
        if (stream.Length == 0)
        {
            return;
        }

        var lastNewline = await FindLastNewlineAsync(stream, cancellationToken).ConfigureAwait(false);
        if (lastNewline == stream.Length - 1)
        {
            return;
        }

        var tailStart = lastNewline + 1;
        var tailLength = checked((int)(stream.Length - tailStart));
        var tail = new byte[tailLength];
        stream.Position = tailStart;
        var read = 0;
        while (read < tail.Length)
        {
            var count = await stream.ReadAsync(tail.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        var text = Encoding.UTF8.GetString(tail, 0, read).TrimEnd('\r');
        var isComplete = false;
        if (text.Length > 0)
        {
            try
            {
                isComplete = JsonSerializer.Deserialize<JsonElement>(text, options).ValueKind != JsonValueKind.Undefined;
            }
            catch (JsonException)
            {
                isComplete = false;
            }
        }

        if (isComplete)
        {
            stream.Position = stream.Length;
            await stream.WriteAsync(Newline, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            stream.SetLength(Math.Max(0, tailStart));
            stream.Position = stream.Length;
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<long> FindLastNewlineAsync(FileStream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var buffer = new byte[BufferSize];
        long offset = 0;
        long lastNewline = -1;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return lastNewline;
            }

            for (var index = 0; index < read; index++)
            {
                if (buffer[index] == (byte)'\n')
                {
                    lastNewline = offset + index;
                }
            }

            offset += read;
        }
    }
}
