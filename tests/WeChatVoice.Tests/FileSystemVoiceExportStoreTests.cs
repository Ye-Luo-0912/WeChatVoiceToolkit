using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Export;

namespace WeChatVoice.Tests;

public sealed class FileSystemVoiceExportStoreTests
{
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
            new VoicePayloadLocator("media", 0, "blob-1"));

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
        Assert.True(skipped.IsSkipped);
        Assert.NotNull(skipped.ExistingOriginalArtifact);

        await store.FinalizeRunAsync(new VoiceExportManifest(DateTimeOffset.UtcNow), CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(store.ExportRoot, "latest.manifest.json")));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(store.ExportRoot, "runs"), "*.manifest.json"));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(store.ExportRoot, "runs"), "*.jsonl"));
        Assert.False(File.Exists(Path.Combine(store.ExportRoot, "manifest.json")));
    }

}
