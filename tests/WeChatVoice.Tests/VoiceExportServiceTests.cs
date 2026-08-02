using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using WeChatVoice.Application;
using WeChatVoice.Core.Errors;
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
        var record = CreateRecord("voice-1");
        var exportRoot = temporary.GetPath("export");
        var store = new FileSystemVoiceExportStore(exportRoot);
        var service = new VoiceExportService(
            new TestVoiceCatalog([(record, () => new MemoryStream(payload, writable: false))]),
            store,
            new FailingDecoder());

        var manifest = await service.ExportAsync(
            new VoiceQuery(),
            new VoiceExportOptions { DecodeToWav = true, MaxDegreeOfParallelism = 1 });

        var entry = Assert.Single(manifest.Entries);
        Assert.Equal("voice-1", entry.MessageId);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(), entry.OriginalSha256);
        Assert.Null(entry.DecodedPath);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(exportRoot, entry.OriginalPath.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Single(manifest.Failures, failure => failure.MessageId == "voice-1" && failure.Stage == "decode");
        Assert.Empty(Directory.EnumerateFiles(exportRoot, "*.wav", SearchOption.AllDirectories));
        Assert.True(File.Exists(Path.Combine(exportRoot, "latest.manifest.json")));
        var journalLines = await File.ReadAllLinesAsync(Assert.Single(Directory.EnumerateFiles(Path.Combine(exportRoot, "runs"), "*.jsonl")));
        var events = journalLines.Select(line => JsonDocument.Parse(line).RootElement.GetProperty("event").GetString()!).ToArray();
        Assert.Equal(["run-started", "item-failed", "item-committed", "processing-completed", "manifest-committed"], events);
        Assert.Equal(ExportRunStatus.CompletedWithFailures, manifest.RunStatus);
    }

    [Fact]
    public async Task ExportAsync_isolates_a_payload_failure_and_removes_the_incomplete_original_file()
    {
        using var temporary = new TestTemporaryDirectory();
        var successful = CreateRecord("voice-good");
        var broken = CreateRecord("voice-broken");
        var exportRoot = temporary.GetPath("export");
        var service = new VoiceExportService(
            new TestVoiceCatalog(
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

    [Fact]
    public async Task ExportAsync_can_add_decoded_artifact_when_original_is_already_verified()
    {
        using var temporary = new TestTemporaryDirectory();
        var payload = new byte[] { 0x31, 0x32, 0x33 };
        var record = CreateRecord(
            "voice-repeat",
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            payload.Length);
        var exportRoot = temporary.GetPath("export");
        var first = new VoiceExportService(new TestVoiceCatalog([(record, () => new MemoryStream(payload, writable: false))]), new FileSystemVoiceExportStore(exportRoot));
        await first.ExportAsync(new VoiceQuery(), new VoiceExportOptions { MaxDegreeOfParallelism = 1 });

        var second = new VoiceExportService(new TestVoiceCatalog([(record, () => new MemoryStream(payload, writable: false))]), new FileSystemVoiceExportStore(exportRoot), new CopyingDecoder());
        var manifest = await second.ExportAsync(new VoiceQuery(), new VoiceExportOptions { DecodeToWav = true, MaxDegreeOfParallelism = 1 });

        var entry = Assert.Single(manifest.Entries);
        Assert.NotNull(entry.DecodedPath);
        Assert.False(entry.WasSkipped);
        Assert.Empty(manifest.Failures);
        Assert.True(File.Exists(Path.Combine(exportRoot, entry.DecodedPath!.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task ExportAsync_rejects_payload_when_committed_hash_differs_from_adapter_expectation()
    {
        using var temporary = new TestTemporaryDirectory();
        var record = CreateRecord("voice-mismatch", new string('a', 64), 4);
        var exportRoot = temporary.GetPath("export");
        var service = new VoiceExportService(
            new TestVoiceCatalog([(record, () => new MemoryStream([1, 2, 3], writable: false))]),
            new FileSystemVoiceExportStore(exportRoot));

        var manifest = await service.ExportAsync(new VoiceQuery(), new VoiceExportOptions { MaxDegreeOfParallelism = 1 });

        Assert.Empty(manifest.Entries);
        Assert.Contains(manifest.Failures, failure => failure.Stage == "source-content-mismatch");
        Assert.Empty(Directory.EnumerateFiles(exportRoot, "*.silk", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(exportRoot, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExportAsync_reads_the_source_blob_exactly_once_for_a_first_export()
    {
        using var temporary = new TestTemporaryDirectory();
        var payload = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        var record = CreateRecord("voice-once");
        var exportRoot = temporary.GetPath("export");
        var counting = new CountingStream(new MemoryStream(payload, writable: false));
        var service = new VoiceExportService(
            new TestVoiceCatalog([(record, () => counting)]),
            new FileSystemVoiceExportStore(exportRoot));

        var manifest = await service.ExportAsync(new VoiceQuery(), new VoiceExportOptions { MaxDegreeOfParallelism = 1 });

        var entry = Assert.Single(manifest.Entries);
        Assert.False(entry.WasSkipped);
        // The DeepScan pre-hash is gone: the source BLOB is consumed exactly
        // once while streaming into the temporary file.
        Assert.Equal(payload.Length, counting.BytesRead);
        Assert.Empty(manifest.Failures);
    }

    [Fact]
    public async Task ExportAsync_rejects_a_changed_selection_before_writing_artifacts()
    {
        using var temporary = new TestTemporaryDirectory();
        var plannedRecord = CreateRecord("voice-planned");
        using var fingerprint = new VoiceResultSetFingerprintBuilder();
        fingerprint.Append(plannedRecord);
        var expectedFingerprint = fingerprint.Complete();
        var exportRoot = temporary.GetPath("export");
        var service = new VoiceExportService(
            new TestVoiceCatalog([(CreateRecord("voice-current"), () => new MemoryStream([1, 2, 3], writable: false))]),
            new FileSystemVoiceExportStore(exportRoot));

        var exception = await Assert.ThrowsAsync<AppFailureException>(() => service.ExportAsync(
            new VoiceQuery(),
            new VoiceExportOptions
            {
                ExpectedResultSetFingerprint = expectedFingerprint,
                ExpectedResultCount = 1,
                ExpectedTotalPayloadBytes = 3,
            }));

        Assert.Equal(ErrorCode.SelectionPlanMismatch, exception.Code);
        Assert.False(Directory.Exists(exportRoot));
    }

    [Fact]
    public async Task ExportAsync_skips_an_existing_item_when_the_source_hash_is_unknown()
    {
        using var temporary = new TestTemporaryDirectory();
        var payload = new byte[] { 0x0a, 0x0b, 0x0c };
        var record = CreateRecord("voice-pending");
        var exportRoot = temporary.GetPath("export");
        var store = new FileSystemVoiceExportStore(exportRoot);

        var first = new VoiceExportService(new TestVoiceCatalog([(record, () => new MemoryStream(payload, writable: false))]), store);
        var firstManifest = await first.ExportAsync(new VoiceQuery(), new VoiceExportOptions { MaxDegreeOfParallelism = 1 });
        Assert.False(Assert.Single(firstManifest.Entries).WasSkipped);

        var secondPayload = new CountingStream(new MemoryStream(payload, writable: false));
        var second = new VoiceExportService(new TestVoiceCatalog([(record, () => secondPayload)]), store);
        var secondManifest = await second.ExportAsync(new VoiceQuery(), new VoiceExportOptions { MaxDegreeOfParallelism = 1 });

        var entry = Assert.Single(secondManifest.Entries);
        Assert.True(entry.WasSkipped);
        Assert.Empty(secondManifest.Failures);
        Assert.Empty(Directory.EnumerateFiles(exportRoot, "*.tmp", SearchOption.AllDirectories));
        // The identity decision read the source once and found a match.
        Assert.Equal(payload.Length, secondPayload.BytesRead);
    }

    [Fact]
    public async Task ExportAsync_skips_without_opening_the_source_when_the_adapter_hash_is_known()
    {
        using var temporary = new TestTemporaryDirectory();
        var payload = new byte[] { 0x11, 0x22, 0x33 };
        var record = CreateRecord(
            "voice-known-hash",
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            payload.Length);
        var exportRoot = temporary.GetPath("export");
        var store = new FileSystemVoiceExportStore(exportRoot);

        var first = new VoiceExportService(new TestVoiceCatalog([(record, () => new MemoryStream(payload, writable: false))]), store);
        await first.ExportAsync(new VoiceQuery(), new VoiceExportOptions { MaxDegreeOfParallelism = 1 });

        var secondPayload = new CountingStream(new MemoryStream(payload, writable: false));
        var second = new VoiceExportService(new TestVoiceCatalog([(record, () => secondPayload)]), store);
        var secondManifest = await second.ExportAsync(new VoiceQuery(), new VoiceExportOptions { MaxDegreeOfParallelism = 1 });

        var entry = Assert.Single(secondManifest.Entries);
        Assert.True(entry.WasSkipped);
        // A trusted source hash lets the store verify the existing artifact
        // without opening the source BLOB at all.
        Assert.Equal(0, secondPayload.BytesRead);
    }

    [Fact]
    public async Task ExportAsync_manifest_inherits_the_full_materialization_provenance()
    {
        using var temporary = new TestTemporaryDirectory();
        var payload = new byte[] { 0x51, 0x52, 0x53 };
        var record = CreateRecord("voice-provenance");
        var exportRoot = temporary.GetPath("export");
        var provenance = new MaterializationProvenance(
            "snapshot-test",
            "materialized-test",
            "sqlcipher-e_sqlcipher-worker",
            "e_sqlcipher-2.1.11-worker-v1",
            new string('b', 64),
            new string('c', 64),
            "weixin-windows-4.1.11.55-wcdb-protected-spec-v2",
            "4.1.11.55",
            new string('d', 64),
            new string('e', 64),
            new string('f', 64));
        var service = new VoiceExportService(
            new TestVoiceCatalog([(record, () => new MemoryStream(payload, writable: false))], provenance),
            new FileSystemVoiceExportStore(exportRoot));

        var manifest = await service.ExportAsync(new VoiceQuery(), new VoiceExportOptions { MaxDegreeOfParallelism = 1 });

        Assert.NotNull(manifest.Provenance);
        Assert.Equal("weixin-windows-4.1.11.55-wcdb-protected-spec-v2", manifest.Provenance.KeyExtractionProfileId);
        Assert.Equal("4.1.11.55", manifest.Provenance.ProcessVersion);
        Assert.Equal(new string('d', 64), manifest.Provenance.ProcessImageSha256);
        Assert.Equal(new string('e', 64), manifest.Provenance.WcdbModuleSha256);
        Assert.Equal(new string('b', 64), manifest.Provenance.BackendBundleSha256);
    }

    private static VoiceRecord CreateRecord(string messageId, string? payloadSha256 = null, long? payloadByteLength = null) => new(
        messageId,
        "contact@example",
        new DateTimeOffset(2026, 7, 31, 6, 0, 0, TimeSpan.Zero),
        VoiceDirection.Incoming,
        new VoicePayloadLocator("media", 0, messageId),
        SnapshotId: "snapshot",
        AdapterId: "adapter",
        AccountId: "account",
        ShardId: "0",
        DataSetId: "dataset",
        AdapterVersion: "1",
        DatabaseFingerprints: ["db-fingerprint"],
        PayloadSha256: payloadSha256,
        PayloadByteLength: payloadByteLength);

    private sealed class TestVoiceCatalog : IVoiceCatalog
    {
        private readonly IReadOnlyList<(VoiceRecord Record, Func<Stream> CreateStream)> _records;

        public TestVoiceCatalog(IReadOnlyList<(VoiceRecord Record, Func<Stream> CreateStream)> records, MaterializationProvenance? provenance = null)
        {
            _records = records;
            Context = new VoiceCatalogContext("dataset", "adapter", "1", "account", ["db-fingerprint"], "snapshot", MaterializationProvenance: provenance);
        }

        public VoiceCatalogContext Context { get; }

        public async IAsyncEnumerable<ContactRecord> QueryContactsAsync(
            ContactQuery query,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<VoiceRecord> QueryVoicesAsync(
            VoiceQuery query,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var (record, _) in _records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return record;
                await Task.Yield();
            }
        }

        public ValueTask<Stream> OpenPayloadAsync(VoicePayloadLocator locator, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = _records.Single(item => item.Record.PayloadLocator?.BlobKey == locator.BlobKey);
            return ValueTask.FromResult(match.CreateStream());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingDecoder : IVoiceDecoder
    {
        public Task DecodeAsync(Stream input, Stream output, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("The test decoder deliberately failed."));
    }

    private sealed class CopyingDecoder : IVoiceDecoder
    {
        public async Task DecodeAsync(Stream input, Stream output, CancellationToken cancellationToken)
        {
            await output.WriteAsync(CreateWave(), cancellationToken);
        }

        private static byte[] CreateWave()
        {
            var data = new byte[] { 0, 0, 0, 0 };
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
            {
                writer.Write("RIFF"u8.ToArray());
                writer.Write(36 + data.Length);
                writer.Write("WAVE"u8.ToArray());
                writer.Write("fmt "u8.ToArray());
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(8000);
                writer.Write(16000);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write("data"u8.ToArray());
                writer.Write(data.Length);
                writer.Write(data);
            }

            return stream.ToArray();
        }
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            BytesRead += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
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
