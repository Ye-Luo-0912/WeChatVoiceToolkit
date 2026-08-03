using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Export;

namespace WeChatVoice.Tests;

public sealed class FileSystemVoiceExportStoreTests
{
    [Fact]
    public async Task BeginRunAsync_uses_a_cross_process_export_root_lock()
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.GetPath("export");
        var firstStore = new FileSystemVoiceExportStore(root);
        var secondStore = new FileSystemVoiceExportStore(root);
        var context = new VoiceCatalogContext("dataset", "adapter", "1", "account", ["db"]);

        await using var first = await firstStore.BeginRunAsync(
            new VoiceExportRunContext("first", context, DateTimeOffset.UtcNow),
            CancellationToken.None);

        await Assert.ThrowsAsync<ExportRootBusyException>(async () =>
            await secondStore.BeginRunAsync(
                new VoiceExportRunContext("second", context, DateTimeOffset.UtcNow),
            CancellationToken.None));
    }

    [Fact]
    public async Task Recovery_cleans_only_stale_unreferenced_staging_directories()
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.GetPath("export");
        var stale = Path.Combine(root, "runs", ".orphan-run.staging");
        var fresh = Path.Combine(root, "runs", ".fresh-run.staging");
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(fresh);
        await File.WriteAllTextAsync(Path.Combine(stale, "partial.tmp"), "stale");
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

        await new FileSystemVoiceExportStore(root).RecoverPendingTransactionsAsync(CancellationToken.None);

        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public async Task Explicit_rollback_is_terminal_and_is_not_resubmitted_as_metadata()
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.GetPath("export");
        var store = new FileSystemVoiceExportStore(root);
        var context = new VoiceCatalogContext("dataset", "adapter", "1", "account", ["db"]);
        var journalPath = string.Empty;

        await using (var journal = await store.BeginRunAsync(
            new VoiceExportRunContext("rolled-back", context, DateTimeOffset.UtcNow),
            CancellationToken.None))
        {
            journalPath = Path.Combine(root, "runs", "rolled-back.jsonl");
            await journal.AppendAsync(
                new VoiceExportJournalEvent("run-started", "rolled-back", DateTimeOffset.UtcNow, Context: context),
                CancellationToken.None);
            await journal.RollbackAsync(CancellationToken.None);
        }

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
        var transaction = JsonSerializer.Deserialize<ExportTransactionDocument>(
            await File.ReadAllTextAsync(Path.Combine(root, "runs", "rolled-back.transaction.json")),
            options);
        Assert.Equal(ExportTransactionState.RolledBack, transaction!.State);
        Assert.Equal("export-rolled-back", transaction.FailureCode);

        await Assert.ThrowsAsync<IOException>(() => store.RecoverRunAsync(journalPath, CancellationToken.None));

        await using (var next = await store.BeginRunAsync(
            new VoiceExportRunContext("next-run", context, DateTimeOffset.UtcNow),
            CancellationToken.None))
        {
            await next.AppendAsync(
                new VoiceExportJournalEvent("run-started", "next-run", DateTimeOffset.UtcNow, Context: context),
                CancellationToken.None);
        }

        Assert.False(File.Exists(Path.Combine(root, "runs", "rolled-back.metadata-commit.json")));
        Assert.False(File.Exists(Path.Combine(root, "manifest.private.json")));
    }

    [Fact]
    public async Task Direct_item_publish_rechecks_the_target_under_the_cross_process_lock()
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.GetPath("export");
        var record = CreateRecord("direct-race");
        var firstStore = new FileSystemVoiceExportStore(root);
        var secondStore = new FileSystemVoiceExportStore(root);

        var first = PublishAsync(firstStore, record, [1, 2, 3]);
        var second = PublishAsync(secondStore, record, [9, 8, 7]);
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Count(static result => result.Succeeded));
        Assert.Equal(1, results.Count(static result => !result.Succeeded));
        var artifactPath = Path.Combine(root, results.Single(static result => result.Succeeded).Path.Replace('/', Path.DirectorySeparatorChar));
        var bytes = await File.ReadAllBytesAsync(artifactPath);
        Assert.True(bytes.AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }) || bytes.AsSpan().SequenceEqual(new byte[] { 9, 8, 7 }));

        static async Task<PublishResult> PublishAsync(
            FileSystemVoiceExportStore store,
            VoiceRecord record,
            byte[] bytes)
        {
            try
            {
                await using var lease = await store.BeginItemAsync(record, ExistingArtifactPolicy.SkipIfHashMatches, CancellationToken.None);
                await using (var output = await lease.OpenOriginalWriteAsync(CancellationToken.None))
                {
                    await output.WriteAsync(bytes);
                }

                var artifact = await lease.CommitOriginalAsync(CancellationToken.None);
                return new PublishResult(true, artifact.RelativePath);
            }
            catch (ExistingArtifactConflictException)
            {
                return new PublishResult(false, string.Empty);
            }
        }
    }

    private sealed record PublishResult(bool Succeeded, string Path);

    [Fact]
    public async Task RecoverPendingTransactionsAsync_publishes_a_moved_artifact_and_replays_its_item_event()
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.GetPath("export");
        var store = new FileSystemVoiceExportStore(root);
        var bytes = new byte[] { 1, 2, 3, 4 };
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var runId = "crashed-run";
        var stagedRelative = $"runs/.{runId}.staging/original/aa/bb/item.silk";
        var finalRelative = "original/aa/bb/item.silk";
        var stagedPath = temporary.GetPath("export", stagedRelative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
        await File.WriteAllBytesAsync(stagedPath, bytes);

        var context = new VoiceCatalogContext("dataset", "adapter", "1", "account", ["db"]);
        var entry = new VoiceExportEntry(
            "message",
            "conversation",
            DateTimeOffset.UtcNow,
            VoiceDirection.Incoming,
            finalRelative,
            bytes.Length,
            sha256,
            null,
            SourceStableKey: "adapter|account|conversation|message|media:message");
        var item = new ExportTransactionItem(
            entry.MessageId,
            entry.SourceStableKey,
            stagedRelative,
            finalRelative,
            null,
            null,
            bytes.Length,
            sha256,
            null,
            null,
            ExportPublishState.Publishing,
            ExportPublishState.NotStarted,
            ExportArtifactState.Missing,
            ExportArtifactState.Missing,
            entry);
        var transaction = new ExportTransactionDocument(
            runId,
            "operation-crashed",
            "selection",
            ExportTransactionState.Publishing,
            DateTimeOffset.UtcNow,
            [item]);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
        var runs = temporary.GetPath("export", "runs");
        Directory.CreateDirectory(runs);
        await File.WriteAllTextAsync(
            Path.Combine(runs, runId + ".transaction.json"),
            JsonSerializer.Serialize(transaction, options),
            Encoding.UTF8);
        await File.WriteAllTextAsync(
            Path.Combine(runs, runId + ".jsonl"),
            JsonSerializer.Serialize(new VoiceExportJournalEvent("run-started", runId, DateTimeOffset.UtcNow, Context: context), options)
                + Environment.NewLine
                + JsonSerializer.Serialize(new VoiceExportJournalEvent("processing-completed", runId, DateTimeOffset.UtcNow, Context: context), options)
                + Environment.NewLine,
            Encoding.UTF8);

        await store.RecoverPendingTransactionsAsync(CancellationToken.None);

        var finalPath = temporary.GetPath("export", finalRelative.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(finalPath));
        var journal = await File.ReadAllLinesAsync(Path.Combine(runs, runId + ".jsonl"));
        Assert.Contains(journal, line => line.Contains("item-committed", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(root, "runs", runId + ".metadata-commit.json")));
        var recoveredTransaction = JsonSerializer.Deserialize<ExportTransactionDocument>(
            await File.ReadAllTextAsync(Path.Combine(runs, runId + ".transaction.json")),
            options);
        Assert.Equal(ExportTransactionState.Completed, recoveredTransaction!.State);
    }

    [Fact]
    public async Task Completed_previous_run_is_not_replayed_when_latest_aliases_belong_to_a_newer_run()
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.GetPath("export");
        var store = new FileSystemVoiceExportStore(root);
        var context = new VoiceCatalogContext("dataset", "adapter", "1", "account", ["db"]);

        await using (var first = await store.BeginRunAsync(
            new VoiceExportRunContext("first-run", context, DateTimeOffset.UtcNow),
            CancellationToken.None))
        {
            await first.AppendAsync(
                new VoiceExportJournalEvent("run-started", "first-run", DateTimeOffset.UtcNow, Context: context),
                CancellationToken.None);
            await first.FinalizeAsync(
                new VoiceExportManifest(DateTimeOffset.UtcNow, RunId: "first-run"),
                CancellationToken.None);
        }

        await using (var second = await store.BeginRunAsync(
            new VoiceExportRunContext("second-run", context, DateTimeOffset.UtcNow),
            CancellationToken.None))
        {
            await second.AppendAsync(
                new VoiceExportJournalEvent("run-started", "second-run", DateTimeOffset.UtcNow, Context: context),
                CancellationToken.None);
            await second.FinalizeAsync(
                new VoiceExportManifest(DateTimeOffset.UtcNow, RunId: "second-run"),
                CancellationToken.None);
        }

        var descriptorOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
        var latestPath = Path.Combine(root, "latest.metadata-commit.json");
        var latest = JsonSerializer.Deserialize<ExportMetadataCommitDescriptor>(
            await File.ReadAllTextAsync(latestPath),
            descriptorOptions);
        Assert.Equal("second-run", latest!.RunId);
        Assert.True(File.Exists(Path.Combine(root, "runs", ExportManifestLayout.RunArtifactIndexFileName("first-run"))));
        Assert.True(File.Exists(Path.Combine(root, "runs", ExportManifestLayout.RunArtifactIndexFileName("second-run"))));

        await store.RecoverPendingTransactionsAsync(CancellationToken.None);

        latest = JsonSerializer.Deserialize<ExportMetadataCommitDescriptor>(
            await File.ReadAllTextAsync(latestPath),
            descriptorOptions);
        Assert.Equal("second-run", latest!.RunId);
    }

    [Theory]
    [InlineData(ExportTransactionFaultPoint.AfterArtifactPublish)]
    [InlineData(ExportTransactionFaultPoint.AfterMetadataCommit)]
    [InlineData(ExportTransactionFaultPoint.AfterManifestCommit)]
    public async Task Fault_injection_leaves_a_recoverable_transaction_without_duplicate_final_artifacts(
        ExportTransactionFaultPoint faultPoint)
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.GetPath("export");
        var store = new FileSystemVoiceExportStore(root, new ThrowOnceFaultInjector(faultPoint));
        var context = new VoiceCatalogContext("dataset", "adapter", "1", "account", ["db"]);
        var record = CreateRecord("fault-injected", Hash([1, 2, 3]), 3);
        var runContext = new VoiceExportRunContext("fault-run", context, DateTimeOffset.UtcNow, "selection");

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await using var journal = await store.BeginRunAsync(runContext, CancellationToken.None);
            await journal.AppendAsync(
                new VoiceExportJournalEvent("run-started", "fault-run", DateTimeOffset.UtcNow, Context: context),
                CancellationToken.None);
            await using var item = await journal.StageItemAsync(record, ExistingArtifactPolicy.SkipIfHashMatches, CancellationToken.None);
            await using (var output = await item.OpenOriginalWriteAsync(CancellationToken.None))
            {
                await output.WriteAsync(new byte[] { 1, 2, 3 });
            }

            var artifact = await item.CommitOriginalAsync(CancellationToken.None);
            await journal.RecordEntryAsync(
                new VoiceExportEntry(
                    record.MessageId,
                    record.ConversationId,
                    record.OccurredAtUtc,
                    record.Direction,
                    artifact.RelativePath,
                    artifact.ByteLength,
                    artifact.Sha256,
                    null,
                    record.SourceStableKey),
                CancellationToken.None);
            await journal.CommitAsync(CancellationToken.None);
            if (faultPoint != ExportTransactionFaultPoint.AfterArtifactPublish)
            {
                await journal.FinalizeAsync(
                    new VoiceExportManifest(DateTimeOffset.UtcNow, RunId: "fault-run"),
                    CancellationToken.None);
            }
        });

        var recovery = new FileSystemVoiceExportStore(root);
        await recovery.RecoverPendingTransactionsAsync(CancellationToken.None);

        var silkFiles = Directory.EnumerateFiles(Path.Combine(root, "original"), "*.silk", SearchOption.AllDirectories).ToArray();
        Assert.Single(silkFiles);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(silkFiles[0]));
        Assert.True(File.Exists(Path.Combine(root, "manifest.private.json")));
        var transaction = JsonSerializer.Deserialize<ExportTransactionDocument>(
            await File.ReadAllTextAsync(Path.Combine(root, "runs", "fault-run.transaction.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
            });
        Assert.Equal(ExportTransactionState.Completed, transaction!.State);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "runs"), "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task BeginItemAsync_uses_only_the_stable_key_for_physical_paths()
    {
        using var temporary = new TestTemporaryDirectory();
        var store = new FileSystemVoiceExportStore(temporary.GetPath("export"));
        var first = new VoiceRecord(
            "message-id",
            "conversation",
            new DateTimeOffset(2020, 1, 2, 0, 0, 0, TimeSpan.Zero),
            VoiceDirection.Incoming,
            new VoicePayloadLocator("media", 0, "blob"),
            AdapterId: "adapter",
            AccountId: "account");
        var second = new VoiceRecord(
            "message-id",
            "conversation",
            new DateTimeOffset(2030, 9, 10, 0, 0, 0, TimeSpan.Zero),
            VoiceDirection.Incoming,
            new VoicePayloadLocator("media", 0, "blob"),
            AdapterId: "adapter",
            AccountId: "account");

        string firstOriginalPath;
        string firstDecodedPath;
        await using (var firstLease = await store.BeginItemAsync(first, ExistingArtifactPolicy.Replace, CancellationToken.None))
        {
            firstOriginalPath = firstLease.OriginalManifestPath;
            firstDecodedPath = firstLease.DecodedManifestPath;
        }

        await using (var secondLease = await store.BeginItemAsync(second, ExistingArtifactPolicy.Replace, CancellationToken.None))
        {
            Assert.Equal(firstOriginalPath, secondLease.OriginalManifestPath);
            Assert.Equal(firstDecodedPath, secondLease.DecodedManifestPath);
            Assert.DoesNotContain("2020", secondLease.OriginalManifestPath, StringComparison.Ordinal);
            Assert.DoesNotContain("2030", secondLease.OriginalManifestPath, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Replace_invalidates_a_decoded_artifact_when_original_content_changes()
    {
        using var temporary = new TestTemporaryDirectory();
        var store = new FileSystemVoiceExportStore(temporary.GetPath("export"));
        var record = new VoiceRecord(
            "message-id",
            "conversation",
            DateTimeOffset.UtcNow,
            VoiceDirection.Incoming,
            new VoicePayloadLocator("media", 0, "blob"),
            AdapterId: "adapter",
            AccountId: "account");

        await using (var first = await store.BeginItemAsync(record, ExistingArtifactPolicy.Replace, CancellationToken.None))
        {
            await using (var original = await first.OpenOriginalWriteAsync(CancellationToken.None))
            {
                await original.WriteAsync(new byte[] { 1, 2, 3 });
            }

            await first.CommitOriginalAsync(CancellationToken.None);
            await using (var decoded = await first.OpenDecodedWriteAsync(CancellationToken.None))
            {
                await decoded.WriteAsync(CreateWave());
            }

            await first.CommitDecodedAsync(CancellationToken.None);
        }

        await using (var replacement = await store.BeginItemAsync(record, ExistingArtifactPolicy.Replace, CancellationToken.None))
        {
            await using (var original = await replacement.OpenOriginalWriteAsync(CancellationToken.None))
            {
                await original.WriteAsync(new byte[] { 9, 8, 7 });
            }

            await replacement.CommitOriginalAsync(CancellationToken.None);
            Assert.Equal(ExportArtifactState.Missing, replacement.DecodedState);
            Assert.Null(replacement.ExistingDecodedArtifact);
        }

        static byte[] CreateWave()
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
            {
                writer.Write("RIFF"u8.ToArray());
                writer.Write(40);
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
                writer.Write(4);
                writer.Write(new byte[4]);
            }

            return stream.ToArray();
        }
    }

    [Fact]
    public async Task BeginItemAsync_owns_stream_commit_and_rollback_without_exposing_absolute_paths()
    {
        using var temporary = new TestTemporaryDirectory();
        var store = new FileSystemVoiceExportStore(temporary.GetPath("export"));
        var record = new VoiceRecord(
            "lease-message",
            "conversation",
            new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero),
            VoiceDirection.Incoming,
            new VoicePayloadLocator("media", 0, "blob-1"),
            AdapterId: "adapter",
            AccountId: "account");

        await using var lease = await store.BeginItemAsync(record, ExistingArtifactPolicy.Fail, CancellationToken.None);
        Assert.False(Path.IsPathRooted(lease.OriginalManifestPath));
        Assert.False(Path.IsPathRooted(lease.DecodedManifestPath));

        await using (var original = await lease.OpenOriginalWriteAsync(CancellationToken.None))
        {
            await original.WriteAsync(new byte[] { 1, 2, 3 });
        }

        var originalArtifact = await lease.CommitOriginalAsync(CancellationToken.None);
        Assert.Equal(3, originalArtifact.ByteLength);
        Assert.Equal(originalArtifact.RelativePath, lease.OriginalManifestPath);

        await using (var decoded = await lease.OpenDecodedWriteAsync(CancellationToken.None))
        {
            await decoded.WriteAsync(new byte[] { 4, 5 });
        }

        var decodedArtifact = await lease.CommitDecodedAsync(CancellationToken.None);
        Assert.Equal(2, decodedArtifact.ByteLength);
        await lease.RollbackAsync(CancellationToken.None);
        Assert.Empty(Directory.EnumerateFiles(store.ExportRoot, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task BeginItemAsync_reuses_same_stable_key_when_existing_hash_matches()
    {
        using var temporary = new TestTemporaryDirectory();
        var store = new FileSystemVoiceExportStore(temporary.GetPath("export"));
        var bytes = new byte[] { 7, 8, 9 };
        var record = new VoiceRecord(
            "message-id",
            "conversation",
            DateTimeOffset.UtcNow,
            VoiceDirection.Incoming,
            new VoicePayloadLocator("media", 0, "blob"),
            PayloadSha256: Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
            SnapshotId: "snapshot",
            AdapterId: "adapter",
            AccountId: "account",
            ShardId: "0");

        await using (var lease = await store.BeginItemAsync(record, ExistingArtifactPolicy.Fail, CancellationToken.None))
        {
            await using var output = await lease.OpenOriginalWriteAsync(CancellationToken.None);
            await output.WriteAsync(bytes);
            await output.FlushAsync();
            // The using scope closes the stream before the commit below.
        }

        // The first lease was intentionally rolled back without committing; the
        // stable reservation is released and no duplicate suffix is allocated.
        await using var second = await store.BeginItemAsync(record, ExistingArtifactPolicy.Fail, CancellationToken.None);
        await using (var output = await second.OpenOriginalWriteAsync(CancellationToken.None))
        {
            await output.WriteAsync(bytes);
        }
        await second.CommitOriginalAsync(CancellationToken.None);
        await second.DisposeAsync();

        await using var skipped = await store.BeginItemAsync(record, ExistingArtifactPolicy.SkipIfHashMatches, CancellationToken.None);
        Assert.Equal(ExportArtifactState.VerifiedExisting, skipped.OriginalState);
        Assert.NotNull(skipped.ExistingOriginalArtifact);

        await using (var journal = await store.BeginRunAsync(
            new VoiceExportRunContext(
                "run-test",
                new VoiceCatalogContext("dataset", "adapter", "1", "account", ["db-fingerprint"]),
                DateTimeOffset.UtcNow),
            CancellationToken.None))
        {
            await journal.AppendAsync(new VoiceExportJournalEvent("run-started", "run-test", DateTimeOffset.UtcNow), CancellationToken.None);
            await journal.FinalizeAsync(new VoiceExportManifest(DateTimeOffset.UtcNow, RunId: "run-test"), CancellationToken.None);
        }
        Assert.True(File.Exists(Path.Combine(store.ExportRoot, "dataset.manifest.json")));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(store.ExportRoot, "runs"), "*.dataset.manifest.json"));
        Assert.True(File.Exists(Path.Combine(store.ExportRoot, "runs", "run-test.jsonl")));
        Assert.False(File.Exists(Path.Combine(store.ExportRoot, "manifest.json")));
    }

    [Fact]
    public async Task RecoverRunAsync_ignores_a_truncated_final_jsonl_line()
    {
        using var temporary = new TestTemporaryDirectory();
        var store = new FileSystemVoiceExportStore(temporary.GetPath("export"));
        var context = new VoiceCatalogContext("dataset", "adapter", "1", "account", ["db-fingerprint"]);
        var entry = new VoiceExportEntry(
            "message",
            "conversation",
            DateTimeOffset.UtcNow,
            VoiceDirection.Incoming,
            "original/aa/bb/key.silk",
            1,
            "hash",
            null,
            SourceStableKey: "adapter|account|conversation|message|media:message");
        string journalPath;
        await using (var journal = await store.BeginRunAsync(new VoiceExportRunContext("recover-run", context, DateTimeOffset.UtcNow), CancellationToken.None))
        {
            journalPath = Path.Combine(store.ExportRoot, "runs", "recover-run.jsonl");
            await journal.AppendAsync(new VoiceExportJournalEvent("run-started", "recover-run", DateTimeOffset.UtcNow, Context: context), CancellationToken.None);
            await journal.AppendAsync(new VoiceExportJournalEvent("item-committed", "recover-run", DateTimeOffset.UtcNow, Entry: entry), CancellationToken.None);
        }

        await File.AppendAllTextAsync(journalPath, "{\"event\":\"item-committed\"");
        var recovered = await store.RecoverRunAsync(journalPath, CancellationToken.None);

        Assert.Single(recovered.Entries);
        Assert.Equal(ExportRunStatus.Failed, recovered.RunStatus);
        Assert.True(File.Exists(Path.Combine(store.ExportRoot, "runs", "recover-run.dataset.manifest.json")));
    }

    [Fact]
    public async Task RecoverRunAsync_preserves_the_full_materialization_provenance()
    {
        using var temporary = new TestTemporaryDirectory();
        var store = new FileSystemVoiceExportStore(temporary.GetPath("export"));
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
        var context = new VoiceCatalogContext("dataset", "adapter", "1", "account", ["db-fingerprint"], MaterializationProvenance: provenance);
        string journalPath;
        await using (var journal = await store.BeginRunAsync(new VoiceExportRunContext("recover-provenance", context, DateTimeOffset.UtcNow), CancellationToken.None))
        {
            journalPath = Path.Combine(store.ExportRoot, "runs", "recover-provenance.jsonl");
            await journal.AppendAsync(new VoiceExportJournalEvent("run-started", "recover-provenance", DateTimeOffset.UtcNow, Context: context), CancellationToken.None);
        }

        var recovered = await store.RecoverRunAsync(journalPath, CancellationToken.None);

        Assert.NotNull(recovered.Provenance);
        Assert.Equal("weixin-windows-4.1.11.55-wcdb-protected-spec-v2", recovered.Provenance.KeyExtractionProfileId);
        Assert.Equal("4.1.11.55", recovered.Provenance.ProcessVersion);
        Assert.Equal(new string('d', 64), recovered.Provenance.ProcessImageSha256);
        Assert.Equal(new string('e', 64), recovered.Provenance.WcdbModuleSha256);
    }

    [Fact]
    public async Task BeginItemAsync_reports_pending_existing_when_the_source_hash_is_unknown()
    {
        using var temporary = new TestTemporaryDirectory();
        var store = new FileSystemVoiceExportStore(temporary.GetPath("export"));
        var record = CreateRecord("pending-1");
        await using (var first = await store.BeginItemAsync(record, ExistingArtifactPolicy.Replace, CancellationToken.None))
        {
            await using (var original = await first.OpenOriginalWriteAsync(CancellationToken.None))
            {
                await original.WriteAsync(new byte[] { 1, 2, 3 });
            }

            await first.CommitOriginalAsync(CancellationToken.None);
        }

        await using var second = await store.BeginItemAsync(record, ExistingArtifactPolicy.SkipIfHashMatches, CancellationToken.None);

        Assert.Equal(ExportArtifactState.PendingExisting, second.OriginalState);
        Assert.NotNull(second.ExistingOriginalArtifact);
    }

    [Fact]
    public async Task Existing_artifact_index_never_trusts_same_metadata_after_in_place_rewrite()
    {
        using var temporary = new TestTemporaryDirectory();
        var store = new FileSystemVoiceExportStore(temporary.GetPath("export"));
        var firstRecord = CreateRecord("index-rewrite");
        string originalPath;
        DateTime originalWriteTime;
        await using (var first = await store.BeginItemAsync(firstRecord, ExistingArtifactPolicy.Replace, CancellationToken.None))
        {
            originalPath = Path.Combine(store.ExportRoot, first.OriginalManifestPath.Replace('/', Path.DirectorySeparatorChar));
            await using (var output = await first.OpenOriginalWriteAsync(CancellationToken.None))
            {
                await output.WriteAsync(new byte[] { 1, 2, 3 });
            }

            await first.CommitOriginalAsync(CancellationToken.None);
            originalWriteTime = File.GetLastWriteTimeUtc(originalPath);
        }

        // Populate the index with the original bytes before the in-place rewrite.
        await using (var indexed = await store.BeginItemAsync(firstRecord, ExistingArtifactPolicy.SkipIfHashMatches, CancellationToken.None))
        {
            Assert.Equal(ExportArtifactState.PendingExisting, indexed.OriginalState);
        }

        await File.WriteAllBytesAsync(originalPath, [4, 5, 6]);
        File.SetLastWriteTimeUtc(originalPath, originalWriteTime);
        var rewrittenHash = Hash([4, 5, 6]);
        var rewrittenRecord = CreateRecord("index-rewrite", rewrittenHash, 3);

        await using var verified = await store.BeginItemAsync(rewrittenRecord, ExistingArtifactPolicy.SkipIfHashMatches, CancellationToken.None);
        Assert.Equal(ExportArtifactState.VerifiedExisting, verified.OriginalState);
        Assert.Equal(rewrittenHash, verified.ExistingOriginalArtifact?.Sha256);
    }

    [Fact]
    public async Task Commit_with_a_matching_computed_artifact_skips_and_cleans_the_temporary_file()
    {
        using var temporary = new TestTemporaryDirectory();
        var store = new FileSystemVoiceExportStore(temporary.GetPath("export"));
        var record = CreateRecord("pending-2");
        await using (var first = await store.BeginItemAsync(record, ExistingArtifactPolicy.Replace, CancellationToken.None))
        {
            await using (var original = await first.OpenOriginalWriteAsync(CancellationToken.None))
            {
                await original.WriteAsync(new byte[] { 4, 5, 6 });
            }

            await first.CommitOriginalAsync(CancellationToken.None);
        }

        await using var second = await store.BeginItemAsync(record, ExistingArtifactPolicy.SkipIfHashMatches, CancellationToken.None);
        await using (var replacement = await second.OpenOriginalWriteAsync(CancellationToken.None))
        {
            await replacement.WriteAsync(new byte[] { 4, 5, 6 });
        }

        var artifact = new ExportArtifact(second.OriginalManifestPath, 3, Hash(new byte[] { 4, 5, 6 }));
        var committed = await second.CommitOriginalAsync(artifact, CancellationToken.None);

        Assert.Equal(ExportArtifactState.VerifiedExisting, second.OriginalState);
        Assert.Equal(Hash(new byte[] { 4, 5, 6 }), committed.Sha256);
        Assert.Equal([4, 5, 6], await File.ReadAllBytesAsync(Path.Combine(store.ExportRoot, committed.RelativePath.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Empty(Directory.EnumerateFiles(store.ExportRoot, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Commit_with_a_different_computed_artifact_reports_a_source_conflict_without_residue()
    {
        using var temporary = new TestTemporaryDirectory();
        var store = new FileSystemVoiceExportStore(temporary.GetPath("export"));
        var record = CreateRecord("pending-3");
        await using (var first = await store.BeginItemAsync(record, ExistingArtifactPolicy.Replace, CancellationToken.None))
        {
            await using (var original = await first.OpenOriginalWriteAsync(CancellationToken.None))
            {
                await original.WriteAsync(new byte[] { 1, 1, 1 });
            }

            await first.CommitOriginalAsync(CancellationToken.None);
        }

        await using var second = await store.BeginItemAsync(record, ExistingArtifactPolicy.SkipIfHashMatches, CancellationToken.None);
        var originalManifestPath = second.OriginalManifestPath;
        await using (var replacement = await second.OpenOriginalWriteAsync(CancellationToken.None))
        {
            await replacement.WriteAsync(new byte[] { 2, 2, 2 });
        }

        var artifact = new ExportArtifact(second.OriginalManifestPath, 3, Hash(new byte[] { 2, 2, 2 }));

        await Assert.ThrowsAsync<SourceContentMismatchException>(() => second.CommitOriginalAsync(artifact, CancellationToken.None));

        Assert.Empty(Directory.EnumerateFiles(store.ExportRoot, "*.tmp", SearchOption.AllDirectories));
        Assert.Equal([1, 1, 1], await File.ReadAllBytesAsync(Path.Combine(store.ExportRoot, originalManifestPath.Replace('/', Path.DirectorySeparatorChar))));
    }

    private sealed class ThrowOnceFaultInjector(ExportTransactionFaultPoint point) : IExportTransactionFaultInjector
    {
        private int _remaining = 1;

        public void ThrowIfRequested(ExportTransactionFaultPoint requested, string runId, string? messageId)
        {
            if (requested == point && Interlocked.Exchange(ref _remaining, 0) == 1)
            {
                throw new IOException($"Injected export transaction fault at {requested} for {runId}/{messageId}.");
            }
        }
    }

    private static string Hash(byte[] bytes)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    private static VoiceRecord CreateRecord(string messageId, string? payloadSha256 = null, long? payloadByteLength = null) => new(
        messageId,
        "contact@example",
        DateTimeOffset.UtcNow,
        VoiceDirection.Incoming,
        new VoicePayloadLocator("media", 0, messageId),
        PayloadSha256: payloadSha256,
        PayloadByteLength: payloadByteLength,
        AdapterId: "adapter",
        AccountId: "account",
        ShardId: "0");

}
