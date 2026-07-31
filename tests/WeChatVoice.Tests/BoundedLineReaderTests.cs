using WeChatVoice.Core.Protocol;

namespace WeChatVoice.Tests;

public sealed class BoundedLineReaderTests
{
    [Fact]
    public async Task Reads_a_crlf_line_without_retaining_the_delimiter()
    {
        var value = await BoundedLineReader.ReadAsync(new StringReader("hello\r\nnext\n"), 16, CancellationToken.None);

        Assert.Equal("hello", value);
    }

    [Fact]
    public async Task Rejects_an_oversized_line_before_waiting_for_a_newline()
    {
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            BoundedLineReader.ReadAsync(new StringReader(new string('x', 1024)), 32, CancellationToken.None).AsTask());

        Assert.Contains("fixed limit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_is_observed_while_waiting_for_input()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BoundedLineReader.ReadAsync(new StringReader("line\n"), 16, cancellation.Token).AsTask());
    }
}
