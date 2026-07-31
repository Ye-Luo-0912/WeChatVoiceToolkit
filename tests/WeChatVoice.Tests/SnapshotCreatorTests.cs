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

        var creator = new SnapshotCreator();
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

        using var serializedManifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(snapshotDirectory, "snapshot.json")));
        Assert.Equal(JsonValueKind.Array, serializedManifest.RootElement.GetProperty("files").ValueKind);
        Assert.Equal(2, serializedManifest.RootElement.GetProperty("files").GetArrayLength());
    }

    [Fact]
    public async Task CreateAsync_rejects_an_output_directory_inside_the_source_tree()
    {
        using var temporary = new TestTemporaryDirectory();
        var sourceDirectory = temporary.CreateDirectory("source");
        temporary.WriteFile(Path.Combine("source", "payload.silk"), new byte[] { 1 });
        var outputDirectory = Path.Combine(sourceDirectory, "snapshot");

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => new SnapshotCreator().CreateAsync(
            new SnapshotRequest(sourceDirectory, outputDirectory),
            CancellationToken.None));

        Assert.Contains("must not", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(outputDirectory));
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
