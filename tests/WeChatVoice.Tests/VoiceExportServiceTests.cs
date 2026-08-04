using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Application;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Audio;
using WeChatVoice.Infrastructure.Export;

namespace WeChatVoice.Tests;

public sealed class VoiceExportServiceTests
{
    [Fact]
    public async Task Export_manifest_inherits_duration_from_the_verified_cache()
    {
        using var temporary = new TestTemporaryDirectory();
        var payload = new byte[] { 0x02, 0x03, 0x04 };
        var payloadHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var record = CreateRecord("voice-duration", payloadHash, payload.Length);
        await using var cache = new JsonlVoiceDurationCache(
            temporary.GetPath("workspace", ".wechatvoice", "duration-cache.jsonl"),
            "silk-wav-decoder-v1");
        await cache.StoreAsync(
            new VoiceDurationCacheEntry(
                new VoiceDurationCacheKey(record.SourceStableKey!, payloadHash, cache.DecoderVersion),
                4321,
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        var service = new VoiceExportService(
            new TestVoiceCatalog([(record, () => new MemoryStream(payload, writable: false))]),
            new FileSystemVoiceExportStore(temporary.GetPath("export")),
            durationCache: cache);

        var manifest = await service.ExportAsync(new VoiceQuery(), new VoiceExportOptions { MaxDegreeOfParallelism = 1 });

        var entry = Assert.Single(manifest.Entries);
        Assert.Equal(4321, entry.DurationMs);
        Assert.False(entry.SelectedForTraining);
        Assert.Equal(TrainingEligibility.Unknown, entry.TrainingEligibility);
        Assert.Equal(UserSelectionState.NotSelected, entry.UserSelectionState);
        Assert.Equal(0, manifest.TotalTrainingDurationMs);
        Assert.Equal(0, manifest.TrainingEntryCount);
        var csvPath = Path.Combine(temporary.GetPath("export"), "dataset.csv");
        Assert.True(File.Exists(csvPath));
        var csv = await File.ReadAllTextAsync(csvPath);
        Assert.Contains("duration_ms", csv, StringComparison.Ordinal);
        Assert.Contains("4321", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("contact@example", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("source_stable_key", csv, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(temporary.GetPath("export"), "manifest.private.json")));
        Assert.True(File.Exists(Path.Combine(temporary.GetPath("export"), "dataset.csv")));
    }

    [Fact]
    public async Task A_second_export_skips_every_matching_content_without_opening_source_payloads()
    {
        using var temporary = new TestTemporaryDirectory();
        var payloadA = new byte[] { 0x01, 0x02, 0x03 };
        var payloadB = new byte[] { 0x04, 0x05, 0x06, 0x07 };
        var recordA = CreateRecord("repeat-a", Hash(payloadA), payloadA.Length);
        var recordB = CreateRecord("repeat-b", Hash(payloadB), payloadB.Length);
        var records = new[] { recordA, recordB };
        var exportRoot = temporary.GetPath("repeat-export");
        var query = new VoiceQuery(Direction: VoiceDirection.Incoming, MaximumResults: 100);

        var first = new VoiceExportService(
            new TestVoiceCatalog(
            [
                (recordA, () => new MemoryStream(payloadA, writable: false)),
                (recordB, () => new MemoryStream(payloadB, writable: false)),
            ]),
            new FileSystemVoiceExportStore(exportRoot));
        var firstManifest = await first.ExportAsync(query, new VoiceExportOptions { MaxDegreeOfParallelism = 1 });
        Assert.Equal(2, firstManifest.Entries.Count);
        Assert.All(firstManifest.Entries, entry => Assert.False(entry.WasSkipped));

        var secondA = new CountingStream(new MemoryStream(payloadA, writable: false));
        var secondB = new CountingStream(new MemoryStream(payloadB, writable: false));
        var second = new VoiceExportService(
            new TestVoiceCatalog(
            [
                (recordA, () => secondA),
                (recordB, () => secondB),
            ]),
            new FileSystemVoiceExportStore(exportRoot));
        var secondManifest = await second.ExportAsync(query, new VoiceExportOptions { MaxDegreeOfParallelism = 1 });

        Assert.Equal(records.Length, secondManifest.Entries.Count);
        Assert.All(secondManifest.Entries, entry => Assert.True(entry.WasSkipped));
        Assert.Empty(secondManifest.Failures);
        Assert.Equal(0, secondA.BytesRead);
        Assert.Equal(0, secondB.BytesRead);
    }
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
        Assert.True(File.Exists(Path.Combine(exportRoot, "dataset.manifest.json")));
        var journalLines = await File.ReadAllLinesAsync(Path.Combine(exportRoot, "runs", manifest.RunId + ".jsonl"));
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
    public async Task ExactAllOrNothing_rolls_back_every_new_artifact_when_one_prepared_item_fails()
    {
        using var temporary = new TestTemporaryDirectory();
        var successful = CreateRecord("exact-good", Hash([0x11, 0x22]), 2);
        var broken = CreateRecord("exact-broken");
        var exportRoot = temporary.GetPath("exact-export");
        var service = new VoiceExportService(
            new TestVoiceCatalog(
            [
                (successful, () => new MemoryStream([0x11, 0x22], writable: false)),
                (broken, () => new FaultingReadStream()),
            ]),
            new FileSystemVoiceExportStore(exportRoot));

        var manifest = await service.ExportAsync(
            new VoiceQuery(),
            new VoiceExportOptions
            {
                CompletionPolicy = ExportCompletionPolicy.ExactAllOrNothing,
                MaxDegreeOfParallelism = 1,
            });

        Assert.Empty(manifest.Entries);
        Assert.Equal(ExportRunStatus.Failed, manifest.RunStatus);
        Assert.Contains(manifest.Failures, failure => failure.MessageId == broken.MessageId);
        Assert.Empty(Directory.EnumerateFiles(exportRoot, "*.silk", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(exportRoot, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExactAllOrNothing_never_commits_a_partial_hundred_item_selection()
    {
        using var temporary = new TestTemporaryDirectory();
        var records = Enumerable.Range(0, 100)
            .Select(index => CreateRecord($"exact-100-{index:D3}", Hash([1, 2, 3]), 3))
            .ToArray();
        var exportRoot = temporary.GetPath("exact-100-export");
        var catalogRecords = records
            .Select(record =>
            {
                Func<Stream> createStream = record.MessageId == "exact-100-037"
                    ? static () => new FaultingReadStream()
                    : static () => new MemoryStream([1, 2, 3], writable: false);
                return (record, createStream);
            })
            .ToArray();
        var service = new VoiceExportService(
            new TestVoiceCatalog(catalogRecords),
            new FileSystemVoiceExportStore(exportRoot));

        var manifest = await service.ExportAsync(
            new VoiceQuery(Direction: VoiceDirection.Incoming, MaximumResults: 100),
            new VoiceExportOptions
            {
                CompletionPolicy = ExportCompletionPolicy.ExactAllOrNothing,
                MaxDegreeOfParallelism = 4,
            });

        Assert.Equal(ExportRunStatus.Failed, manifest.RunStatus);
        Assert.Empty(manifest.Entries);
        Assert.Contains(manifest.Failures, failure => failure.MessageId == "exact-100-037");
        Assert.Empty(Directory.EnumerateFiles(exportRoot, "*.silk", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(exportRoot, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Export_transaction_identity_uses_source_stable_key_when_message_ids_repeat()
    {
        using var temporary = new TestTemporaryDirectory();
        var first = CreateRecord("same-message", Hash([1, 2, 3]), 3, payloadBlobKey: "blob-a");
        var second = CreateRecord("same-message", Hash([4, 5, 6]), 3, payloadBlobKey: "blob-b");
        var exportRoot = temporary.GetPath("duplicate-message-export");
        var service = new VoiceExportService(
            new TestVoiceCatalog(
            [
                (first, static () => new MemoryStream([1, 2, 3], writable: false)),
                (second, static () => new MemoryStream([4, 5, 6], writable: false)),
            ]),
            new FileSystemVoiceExportStore(exportRoot));

        var manifest = await service.ExportAsync(
            new VoiceQuery(Direction: VoiceDirection.Incoming),
            new VoiceExportOptions
            {
                CompletionPolicy = ExportCompletionPolicy.ExactAllOrNothing,
                MaxDegreeOfParallelism = 1,
            });

        Assert.Equal(ExportRunStatus.Completed, manifest.RunStatus);
        Assert.Equal(2, manifest.Entries.Count);
        Assert.All(manifest.Entries, entry => Assert.Equal("same-message", entry.MessageId));
        Assert.Equal(2, manifest.Entries.Select(entry => entry.SourceStableKey).Distinct(StringComparer.Ordinal).Count());
        var transaction = JsonSerializer.Deserialize<ExportTransactionDocument>(
            await File.ReadAllTextAsync(Path.Combine(exportRoot, "runs", manifest.RunId + ".transaction.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
            });
        Assert.Equal(2, transaction!.Items.Select(item => item.TransactionKey).Distinct(StringComparer.Ordinal).Count());
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
    public async Task ExportAsync_reuses_the_prepared_selection_instead_of_requerying()
    {
        using var temporary = new TestTemporaryDirectory();
        var plannedPayload = new byte[] { 1, 2, 3 };
        var currentPayload = new byte[] { 4, 5, 6, 7 };
        var planned = CreateRecord("voice-planned-after-preflight", Hash(plannedPayload), plannedPayload.Length);
        var current = CreateRecord("voice-current-after-preflight", Hash(currentPayload), currentPayload.Length);
        using var fingerprint = new VoiceResultSetFingerprintBuilder();
        fingerprint.Append(planned);
        var expectedFingerprint = fingerprint.Complete();
        var exportRoot = temporary.GetPath("export");
        var catalog = new SequencedVoiceCatalog(
            [
                (planned, () => new MemoryStream(plannedPayload, writable: false)),
            ],
            [
                (current, () => new MemoryStream(currentPayload, writable: false)),
            ]);
        var service = new VoiceExportService(catalog, new FileSystemVoiceExportStore(exportRoot));

        var manifest = await service.ExportAsync(
            new VoiceQuery(),
            new VoiceExportOptions
            {
                ExpectedResultSetFingerprint = expectedFingerprint,
                ExpectedResultCount = 1,
                ExpectedTotalPayloadBytes = plannedPayload.Length,
                MaxDegreeOfParallelism = 1,
            });

        Assert.Equal(1, catalog.QueryCount);
        var entry = Assert.Single(manifest.Entries);
        Assert.Equal(planned.MessageId, entry.MessageId);
        Assert.Equal(plannedPayload, await File.ReadAllBytesAsync(Path.Combine(exportRoot, entry.OriginalPath.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Empty(manifest.Failures);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(exportRoot, "runs"), "*.staging", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task ExportAsync_consumes_a_prepared_selection_without_resolving_duration_twice()
    {
        using var temporary = new TestTemporaryDirectory();
        var payload = new byte[] { 0x41, 0x42, 0x43 };
        var record = CreateRecord("voice-prepared-duration", Hash(payload), payload.Length);
        using var fingerprint = new VoiceResultSetFingerprintBuilder();
        fingerprint.Append(record);
        var resolver = new CountingDurationResolver(3210);
        var service = new VoiceExportService(
            new TestVoiceCatalog([(record, () => new MemoryStream(payload, writable: false))]),
            new FileSystemVoiceExportStore(temporary.GetPath("export")),
            durationResolver: resolver);

        var manifest = await service.ExportAsync(
            new VoiceQuery(ResolveDuration: true),
            new VoiceExportOptions
            {
                ExpectedResultSetFingerprint = fingerprint.Complete(),
                ExpectedResultCount = 1,
                ExpectedTotalPayloadBytes = payload.Length,
                MaxDegreeOfParallelism = 1,
            });

        Assert.Equal(1, resolver.CallCount);
        Assert.Equal(3210, Assert.Single(manifest.Entries).DurationMs);
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

    [Fact]
    public async Task Export_manifest_preserves_account_evidence_and_user_confirmation_separately()
    {
        using var temporary = new TestTemporaryDirectory();
        var exportRoot = temporary.CreateDirectory("export");
        var identity = new AccountIdentity(
            AccountIdentityState.Candidate,
            null,
            UserConfirmationState.Confirmed);
        var service = new VoiceExportService(
            new TestVoiceCatalog(
                [(CreateRecord("identity", payloadByteLength: 10), () => new MemoryStream("#!SILK_V3"u8.ToArray(), writable: false))],
                accountIdentity: identity),
            new FileSystemVoiceExportStore(exportRoot));

        var manifest = await service.ExportAsync(new VoiceQuery(), new VoiceExportOptions { MaxDegreeOfParallelism = 1 });

        Assert.Equal(AccountIdentityState.Candidate, manifest.AccountIdentity.State);
        Assert.Equal(UserConfirmationState.Confirmed, manifest.AccountIdentity.UserConfirmation);
        Assert.Null(manifest.AccountIdentity.ConfirmedBy);
    }

    private static VoiceRecord CreateRecord(string messageId, string? payloadSha256 = null, long? payloadByteLength = null, string? payloadBlobKey = null) => new(
        messageId,
        "contact@example",
        new DateTimeOffset(2026, 7, 31, 6, 0, 0, TimeSpan.Zero),
        VoiceDirection.Incoming,
        new VoicePayloadLocator("media", 0, payloadBlobKey ?? messageId),
        SnapshotId: "snapshot",
        AdapterId: "adapter",
        AccountId: "account",
        ShardId: "0",
        DataSetId: "dataset",
        AdapterVersion: "1",
        DatabaseFingerprints: ["db-fingerprint"],
        PayloadSha256: payloadSha256,
        PayloadByteLength: payloadByteLength);

    private static string Hash(byte[] payload)
        => Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private sealed class TestVoiceCatalog : IVoiceCatalog
    {
        private readonly IReadOnlyList<(VoiceRecord Record, Func<Stream> CreateStream)> _records;

        public TestVoiceCatalog(
            IReadOnlyList<(VoiceRecord Record, Func<Stream> CreateStream)> records,
            MaterializationProvenance? provenance = null,
            AccountIdentity? accountIdentity = null)
        {
            _records = records;
            Context = new VoiceCatalogContext(
                "dataset",
                "adapter",
                "1",
                "account",
                ["db-fingerprint"],
                "snapshot",
                MaterializationProvenance: provenance,
                AccountIdentity: accountIdentity);
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

    private sealed class SequencedVoiceCatalog : IVoiceCatalog
    {
        private readonly IReadOnlyList<(VoiceRecord Record, Func<Stream> CreateStream)>[] _sequences;
        private int _queryCount;

        public SequencedVoiceCatalog(params IReadOnlyList<(VoiceRecord Record, Func<Stream> CreateStream)>[] sequences)
        {
            _sequences = sequences;
            Context = new VoiceCatalogContext("dataset", "adapter", "1", "account", ["db-fingerprint"], "snapshot");
        }

        public VoiceCatalogContext Context { get; }

        public int QueryCount => Volatile.Read(ref _queryCount);

        public async IAsyncEnumerable<ContactRecord> QueryContactsAsync(ContactQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<VoiceRecord> QueryVoicesAsync(VoiceQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var index = Math.Min(Interlocked.Increment(ref _queryCount) - 1, _sequences.Length - 1);
            foreach (var (record, _) in _sequences[index])
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return record;
                await Task.Yield();
            }
        }

        public ValueTask<Stream> OpenPayloadAsync(VoicePayloadLocator locator, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var sequence in _sequences)
            {
                foreach (var item in sequence)
                {
                    if (item.Record.PayloadLocator?.BlobKey == locator.BlobKey)
                    {
                        return ValueTask.FromResult(item.CreateStream());
                    }
                }
            }

            throw new InvalidDataException("Test payload was not found.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingDecoder : IVoiceDecoder
    {
        public Task DecodeAsync(Stream input, Stream output, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("The test decoder deliberately failed."));
    }

    private sealed class CountingDurationResolver(long duration) : IVoiceDurationResolver
    {
        public int CallCount { get; private set; }

        public Task<long?> ResolveAsync(IVoiceCatalog catalog, VoiceRecord record, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<long?>(duration);
        }
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
