using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Audio;

namespace WeChatVoice.Tests;

public sealed class DecoderVoiceDurationResolverTests
{
    [Fact]
    public async Task Resolver_reads_duration_from_valid_pcm_wav_without_persisting_output()
    {
        var catalog = new FakeCatalog();
        await using var resolver = new DecoderVoiceDurationResolver(new FixedWavDecoder(48000));
        var record = new VoiceRecord("m1", "peer", DateTimeOffset.UtcNow, VoiceDirection.Incoming,
            new VoicePayloadLocator("media", 0, "1"), PayloadState: VoicePayloadState.Linked);

        var duration = await resolver.ResolveAsync(catalog, record, CancellationToken.None);

        Assert.Equal(1000, duration);
    }

    private sealed class FixedWavDecoder(int dataBytes) : IVoiceDecoder
    {
        public async Task DecodeAsync(Stream input, Stream output, CancellationToken cancellationToken)
        {
            var wav = new byte[44 + dataBytes];
            "RIFF"u8.CopyTo(wav);
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), (uint)(wav.Length - 8));
            "WAVE"u8.CopyTo(wav.AsSpan(8));
            "fmt "u8.CopyTo(wav.AsSpan(12));
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16), 16);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24), 24000);
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28), 48000);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), 16);
            "data"u8.CopyTo(wav.AsSpan(36));
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), (uint)dataBytes);
            await output.WriteAsync(wav, cancellationToken);
        }
    }

    private sealed class FakeCatalog : IVoiceCatalog
    {
        public VoiceCatalogContext Context { get; } = new("dataset", "adapter", "1", "account", ["fingerprint"]);
        public async IAsyncEnumerable<ContactRecord> QueryContactsAsync(ContactQuery query, [EnumeratorCancellation] CancellationToken cancellationToken) { await Task.CompletedTask; yield break; }
        public async IAsyncEnumerable<VoiceRecord> QueryVoicesAsync(VoiceQuery query, [EnumeratorCancellation] CancellationToken cancellationToken) { await Task.CompletedTask; yield break; }
        public ValueTask<Stream> OpenPayloadAsync(VoicePayloadLocator locator, CancellationToken cancellationToken)
            => ValueTask.FromResult<Stream>(new MemoryStream("#!SILK_V3"u8.ToArray()));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
