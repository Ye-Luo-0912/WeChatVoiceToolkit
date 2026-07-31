using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using WeChatVoice.Application;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Export;

namespace WeChatVoice.Tests;

public sealed class VoiceExportServiceTests
{
    [Fact]
    public async Task ExportAsync_preserves_original_silk_when_optional_decoding_fails()
    {
        using var temporary = new TestTemporaryDirectory();
        var payload = new byte[] { 0x02, 0x03, 0x04, 0x05 };
        var message = CreateMessage("voice-1");
        var exportRoot = temporary.GetPath("export");
        var store = new FileSystemVoiceExportStore(exportRoot);
        var service = new VoiceExportService(
            new TestVoiceSource([(message, () => new MemoryStream(payload, writable: false))]),
            store,
            new FailingDecoder());

        var manifest = await service.ExportAsync(
            new VoiceQuery(),
            new VoiceExportOptions { DecodeToWav = true, MaxDegreeOfParallelism = 1 });

        var entry = Assert.Single(manifest.Entries);
        Assert.Equal("voice-1", entry.MessageId);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)), entry.OriginalSha256);
        Assert.Null(entry.DecodedPath);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(exportRoot, entry.OriginalPath.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Single(manifest.Failures, failure => failure.MessageId == "voice-1" && failure.Stage == "decode");
        Assert.Empty(Directory.EnumerateFiles(exportRoot, "*.wav", SearchOption.AllDirectories));
        Assert.True(File.Exists(Path.Combine(exportRoot, "latest.manifest.json")));
    }

    [Fact]
    public async Task ExportAsync_isolates_a_payload_failure_and_removes_the_incomplete_original_file()
    {
        using var temporary = new TestTemporaryDirectory();
        var successful = CreateMessage("voice-good");
        var broken = CreateMessage("voice-broken");
        var exportRoot = temporary.GetPath("export");
        var service = new VoiceExportService(
            new TestVoiceSource(
            [
                (successful, () => new MemoryStream(new byte[] { 0x11, 0x22 }, writable: false)),
                (broken, () => new FaultingReadStream()),
            ]),
            new FileSystemVoiceExportStore(exportRoot));

        var manifest = await service.ExportAsync(
            new VoiceQuery(),
            new VoiceExportOptions { MaxDegreeOfParallelism = 1 });

        var entry = Assert.Single(manifest.Entries);
        Assert.Equal("voice-good", entry.MessageId);
        Assert.Single(manifest.Failures, failure => failure.MessageId == "voice-broken" && failure.Stage == "export");
        Assert.Single(Directory.EnumerateFiles(exportRoot, "*.silk", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(exportRoot, "*.tmp", SearchOption.AllDirectories));
    }

    private static VoiceMessage CreateMessage(string messageId) => new(
        messageId,
        "contact@example",
        new DateTimeOffset(2026, 7, 31, 6, 0, 0, TimeSpan.Zero),
        VoiceDirection.Incoming);

    private sealed class TestVoiceSource : IVoiceSource
    {
        private readonly IReadOnlyList<(VoiceMessage Message, Func<Stream> CreateStream)> _messages;

        public TestVoiceSource(IReadOnlyList<(VoiceMessage Message, Func<Stream> CreateStream)> messages)
        {
            _messages = messages;
        }

        public async IAsyncEnumerable<VoiceMessage> QueryAsync(
            VoiceQuery query,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var (message, _) in _messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return message;
                await Task.Yield();
            }
        }

        public ValueTask<Stream> OpenPayloadAsync(VoiceMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = _messages.Single(item => item.Message.MessageId == message.MessageId);
            return ValueTask.FromResult(match.CreateStream());
        }
    }

    private sealed class FailingDecoder : IVoiceDecoder
    {
        public Task DecodeAsync(string inputPath, string outputPath, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("The test decoder deliberately failed."));
    }

    private sealed class FaultingReadStream : Stream
    {
        private bool _returnedData;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("The test payload stream failed.");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_returnedData)
            {
                _returnedData = true;
                buffer.Span[0] = 0x7f;
                return ValueTask.FromResult(1);
            }

            return ValueTask.FromException<int>(new IOException("The test payload stream failed."));
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
