using WeChatVoice.Core.Protocol;

namespace WeChatVoice.Tests;

public sealed class BoundedLineReaderTests
{
    [Fact]
    public async Task Reads_a_crlf_line_without_retaining_the_delimiter()
    {
        using var reader = new BoundedLineReader(new StringReader("hello\r\nnext\n"), 16);
        var value = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal("hello", value);
        Assert.Equal("next", await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Preserves_two_messages_from_one_underlying_read_and_reports_eof()
    {
        using var reader = new BoundedLineReader(new StringReader("first\nsecond\n"), 32);

        Assert.Equal("first", await reader.ReadAsync(CancellationToken.None));
        Assert.Equal("second", await reader.ReadAsync(CancellationToken.None));
        Assert.Null(await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Reads_fragmented_input_across_multiple_underlying_reads()
    {
        using var reader = new BoundedLineReader(new FragmentedTextReader("alpha\r\nbeta\n", 2), 32);

        Assert.Equal("alpha", await reader.ReadAsync(CancellationToken.None));
        Assert.Equal("beta", await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_an_oversized_line_before_waiting_for_a_newline()
    {
        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            using var reader = new BoundedLineReader(new StringReader(new string('x', 1024)), 32);
            await reader.ReadAsync(CancellationToken.None);
        });

        Assert.Contains("fixed limit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_is_observed_while_waiting_for_input()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        using var reader = new BoundedLineReader(new StringReader("line\n"), 16);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(cancellation.Token).AsTask());
    }

    [Fact]
    public async Task Rejects_eof_after_an_undelimited_partial_line()
    {
        using var reader = new BoundedLineReader(new StringReader("partial"), 32);

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(CancellationToken.None).AsTask());
    }

    private sealed class FragmentedTextReader(string value, int chunkSize) : TextReader
    {
        private readonly StringReader _inner = new(value);

        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await _inner.ReadAsync(buffer[..Math.Min(chunkSize, buffer.Length)], cancellationToken).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
