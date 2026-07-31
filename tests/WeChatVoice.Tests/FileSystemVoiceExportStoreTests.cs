using System.Text.Json;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Export;

namespace WeChatVoice.Tests;

public sealed class FileSystemVoiceExportStoreTests
{
    [Fact]
    public async Task CreatePathsAsync_keeps_media_destinations_under_the_configured_root_and_reserves_unique_names()
    {
        using var temporary = new TestTemporaryDirectory();
        var exportRoot = temporary.GetPath("export");
        var store = new FileSystemVoiceExportStore(exportRoot);
        var message = new VoiceMessage(
            "../untrusted:message?",
            "conversation",
            new DateTimeOffset(2026, 7, 31, 9, 0, 0, TimeSpan.FromHours(8)),
            VoiceDirection.Incoming);

        var first = await store.CreatePathsAsync(message, CancellationToken.None);
        var second = await store.CreatePathsAsync(message, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(exportRoot), store.ExportRoot);
        AssertPathIsUnderRoot(exportRoot, first.OriginalFilePath);
        AssertPathIsUnderRoot(exportRoot, first.DecodedFilePath);
        AssertPathIsUnderRoot(exportRoot, second.OriginalFilePath);
        AssertPathIsUnderRoot(exportRoot, second.DecodedFilePath);
        Assert.NotEqual(first.OriginalFilePath, second.OriginalFilePath);
        Assert.NotEqual(first.DecodedFilePath, second.DecodedFilePath);
        Assert.StartsWith("original/2026/07/", first.OriginalManifestPath, StringComparison.Ordinal);
        Assert.StartsWith("decoded/2026/07/", first.DecodedManifestPath, StringComparison.Ordinal);
        Assert.DoesNotContain("..", first.OriginalManifestPath, StringComparison.Ordinal);
        Assert.False(File.Exists(first.OriginalFilePath));
        Assert.False(File.Exists(first.DecodedFilePath));
        Assert.True(Directory.Exists(Path.GetDirectoryName(first.OriginalFilePath)));
        Assert.True(Directory.Exists(Path.GetDirectoryName(first.DecodedFilePath)));
    }

    [Fact]
    public async Task Store_writes_media_and_manifest_but_refuses_paths_owned_by_another_root()
    {
        using var temporary = new TestTemporaryDirectory();
        var exportRoot = temporary.GetPath("export");
        var store = new FileSystemVoiceExportStore(exportRoot);
        var message = new VoiceMessage(
            "message-1",
            "conversation-1",
            new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero),
            VoiceDirection.Outgoing);
        var paths = await store.CreatePathsAsync(message, CancellationToken.None);
        var originalBytes = new byte[] { 0x02, 0x03, 0x04 };
        var decodedBytes = new byte[] { 0x52, 0x49, 0x46, 0x46 };

        await store.WriteOriginalAsync(paths, new MemoryStream(originalBytes, writable: false), CancellationToken.None);
        await store.WriteDecodedAsync(paths, new MemoryStream(decodedBytes, writable: false), CancellationToken.None);

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(paths.OriginalFilePath));
        Assert.Equal(decodedBytes, await File.ReadAllBytesAsync(paths.DecodedFilePath));

        var manifest = new VoiceExportManifest(
            DateTimeOffset.UtcNow,
            [new VoiceExportEntry(
                message.MessageId,
                message.ConversationId,
                message.OccurredAtUtc,
                message.Direction,
                paths.OriginalManifestPath,
                originalBytes.Length,
                "abc123",
                paths.DecodedManifestPath)]);
        await store.WriteManifestAsync(manifest, CancellationToken.None);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(exportRoot, "manifest.json")));
        var entry = Assert.Single(document.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal(paths.OriginalManifestPath, entry.GetProperty("originalPath").GetString());
        Assert.Equal(paths.DecodedManifestPath, entry.GetProperty("decodedPath").GetString());

        var outsideDirectory = temporary.CreateDirectory("outside");
        var outsideOriginal = Path.Combine(outsideDirectory, "outside.silk");
        var outsideDecoded = Path.Combine(outsideDirectory, "outside.wav");
        var outsidePaths = new VoiceExportPaths(
            outsideOriginal,
            outsideDecoded,
            "original/outside.silk",
            "decoded/outside.wav");

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteOriginalAsync(
            outsidePaths,
            new MemoryStream(new byte[] { 0x01 }, writable: false),
            CancellationToken.None));
        Assert.False(File.Exists(outsideOriginal));
    }

    private static void AssertPathIsUnderRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        Assert.False(Path.IsPathRooted(relative));
        Assert.DoesNotContain("..", relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }
}
