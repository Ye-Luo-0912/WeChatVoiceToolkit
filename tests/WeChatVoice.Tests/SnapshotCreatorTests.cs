using System.Security.Cryptography;
using System.Text.Json;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Snapshots;

namespace WeChatVoice.Tests;

public sealed class SnapshotCreatorTests
{
    [Fact]
    public async Task CreateAsync_copies_nested_regular_files_and_records_integrity_metadata()
    {
        using var temporary = new TestTemporaryDirectory();
        var sourceDirectory = temporary.CreateDirectory("source");
        var snapshotDirectory = temporary.GetPath("snapshot");
        var topLevelContents = new byte[] { 0x01, 0x02, 0xA0 };
        var nestedContents = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        var topLevelSource = temporary.WriteFile(Path.Combine("source", "root.silk"), topLevelContents);
        var nestedSource = temporary.WriteFile(Path.Combine("source", "media", "2026", "nested.db-wal"), nestedContents);

        File.SetLastWriteTimeUtc(topLevelSource, new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(nestedSource, new DateTime(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc));

        var creator = new SnapshotCreator(new FixedSnapshotActivityProbe(new SnapshotSourceActivity(false)));
        var manifest = await creator.CreateAsync(
            new SnapshotRequest(sourceDirectory, snapshotDirectory),
            CancellationToken.None);

        Assert.Equal(Path.GetFullPath(sourceDirectory), manifest.SourceDirectory);
        Assert.Equal(Path.GetFullPath(snapshotDirectory), manifest.SnapshotDirectory);
        Assert.Equal(2, manifest.Files.Count);

        var records = manifest.Files.ToDictionary(record => record.RelativePath, StringComparer.Ordinal);
        AssertFileRecord(records["root.silk"], topLevelContents, topLevelSource);
        AssertFileRecord(records["media/2026/nested.db-wal"], nestedContents, nestedSource);

        Assert.Equal(topLevelContents, await File.ReadAllBytesAsync(Path.Combine(snapshotDirectory, "root.silk")));
        Assert.Equal(nestedContents, await File.ReadAllBytesAsync(Path.Combine(snapshotDirectory, "media", "2026", "nested.db-wal")));
        Assert.Equal(
            File.GetLastWriteTimeUtc(topLevelSource),
            File.GetLastWriteTimeUtc(Path.Combine(snapshotDirectory, "root.silk")));

        using var serializedManifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(snapshotDirectory, ".wechatvoice", "snapshot-manifest.json")));
        Assert.Equal(JsonValueKind.Array, serializedManifest.RootElement.GetProperty("files").ValueKind);
        Assert.Equal(2, serializedManifest.RootElement.GetProperty("files").GetArrayLength());
        Assert.False(File.Exists(Path.Combine(snapshotDirectory, "snapshot.json")));
    }

    [Fact]
    public async Task CreateAsync_rejects_an_output_directory_inside_the_source_tree()
    {
        using var temporary = new TestTemporaryDirectory();
        var sourceDirectory = temporary.CreateDirectory("source");
        temporary.WriteFile(Path.Combine("source", "payload.silk"), new byte[] { 1 });
        var outputDirectory = Path.Combine(sourceDirectory, "snapshot");

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => new SnapshotCreator(new FixedSnapshotActivityProbe(new SnapshotSourceActivity(false))).CreateAsync(
            new SnapshotRequest(sourceDirectory, outputDirectory),
            CancellationToken.None));

        Assert.Contains("must not", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(outputDirectory));
    }

    [Fact]
    public async Task CreateAsync_excludes_source_metadata_files_and_marks_live_opt_in()
    {
        using var temporary = new TestTemporaryDirectory();
        var sourceDirectory = temporary.CreateDirectory("source");
        temporary.WriteFile(Path.Combine("source", "payload.db"), [1, 2, 3]);
        temporary.WriteFile(Path.Combine("source", "snapshot.json"), [9]);
        temporary.WriteFile(Path.Combine("source", ".wechatvoice", "snapshot-manifest.json"), [8]);
        var snapshotDirectory = temporary.GetPath("snapshot");

        var creator = new SnapshotCreator(new FixedSnapshotActivityProbe(new SnapshotSourceActivity(true, ["WeChatAppEx"])));
        var manifest = await creator.CreateAsync(
            new SnapshotRequest(sourceDirectory, snapshotDirectory, AllowLiveSource: true),
            CancellationToken.None);

        Assert.True(manifest.PotentiallyInconsistent);
        Assert.Contains("WeChatAppEx", manifest.SourceProcessNames);
        Assert.DoesNotContain(manifest.Files, file => file.RelativePath is "snapshot.json" or ".wechatvoice/snapshot-manifest.json");
        Assert.True(File.Exists(Path.Combine(snapshotDirectory, ".wechatvoice", "snapshot-manifest.json")));
    }

    [Fact]
    public async Task CreateAsync_rejects_live_source_without_explicit_opt_in()
    {
        using var temporary = new TestTemporaryDirectory();
        var sourceDirectory = temporary.CreateDirectory("source");
        temporary.WriteFile(Path.Combine("source", "payload.db"), [1]);

        await Assert.ThrowsAsync<LiveSnapshotSourceException>(() => new SnapshotCreator(
            new FixedSnapshotActivityProbe(new SnapshotSourceActivity(true, ["WeChat"]))
            ).CreateAsync(new SnapshotRequest(sourceDirectory, temporary.GetPath("snapshot")), CancellationToken.None));
    }

    private sealed class FixedSnapshotActivityProbe : WeChatVoice.Core.Ports.ISnapshotSourceActivityProbe
    {
        private readonly SnapshotSourceActivity _activity;

        public FixedSnapshotActivityProbe(SnapshotSourceActivity activity) => _activity = activity;

        public SnapshotSourceActivity Probe() => _activity;
    }

    private static void AssertFileRecord(SnapshotFileRecord record, byte[] expectedContents, string sourcePath)
    {
        Assert.Equal(expectedContents.LongLength, record.ByteLength);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(expectedContents)).ToLowerInvariant(),
            record.Sha256);
        Assert.Equal(TimeSpan.Zero, record.SourceLastWriteTimeUtc.Offset);
        Assert.Equal(File.GetLastWriteTimeUtc(sourcePath), record.SourceLastWriteTimeUtc.UtcDateTime);
    }
}
