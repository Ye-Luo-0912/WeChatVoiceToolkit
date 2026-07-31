using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Export;

namespace WeChatVoice.Tests;

public sealed class FileSystemVoiceExportStoreTests
{
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
        Assert.True(File.Exists(Path.Combine(store.ExportRoot, "latest.manifest.json")));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(store.ExportRoot, "runs"), "*.manifest.json"));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(store.ExportRoot, "runs"), "*.jsonl"));
        Assert.False(File.Exists(Path.Combine(store.ExportRoot, "manifest.json")));
    }

    [Fact]
    public async Task RecoverRunAsync_ignores_a_truncated_final_jsonl_line()
    {
        using var temporary = new TestTemporaryDirectory();
        var store = new FileSystemVoiceExportStore(temporary.GetPath("export"));
        var context = new VoiceCatalogContext("dataset", "adapter", "1", "account", ["db-fingerprint"]);
        var entry = new VoiceExportEntry("message", "conversation", DateTimeOffset.UtcNow, VoiceDirection.Incoming, "original/aa/bb/key.silk", 1, "hash", null);
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
        Assert.True(File.Exists(Path.Combine(store.ExportRoot, "runs", "recover-run.manifest.json")));
    }

}
