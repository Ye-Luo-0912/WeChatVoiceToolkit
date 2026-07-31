using System.Security.Cryptography;
using WeChatVoice.Core.Models;
using WeChatVoice.FakeMaterializer;
using WeChatVoice.Infrastructure.Materialization;

namespace WeChatVoice.Tests;

public sealed class DatabaseMaterializerTests
{
    [Fact]
    public async Task MaterializeAsync_accepts_explicit_complete_mapping_and_plain_sqlite_outputs()
    {
        using var temporary = new TestTemporaryDirectory();
        var snapshot = await CreateVerifiedSnapshotAsync(temporary, "success");
        var output = temporary.GetPath("materialized");

        var result = await CreateMaterializer().MaterializeAsync(snapshot, new MaterializationOptions(output), CancellationToken.None);

        Assert.Equal(3, result.Result.Databases.Count);
        Assert.All(result.Result.Databases, database => Assert.Equal(MaterializationDatabaseStatus.CopiedAsPlaintext, database.Status));
        Assert.True(File.Exists(result.Result.ManifestPath));
        Assert.All(result.Result.Databases, database => Assert.True(File.Exists(Path.Combine(output, database.OutputRelativePath))));
    }

    [Theory]
    [InlineData("missing", "missing one or more required")]
    [InlineData("extra", "not covered by its explicit output manifest")]
    [InlineData("invalid", "not a plain SQLite database")]
    [InlineData("duplicate", "maps a source database more than once")]
    [InlineData("unknown-source", "unknown source database")]
    public async Task MaterializeAsync_rejects_invalid_backend_outputs(string mode, string expectedMessage)
    {
        using var temporary = new TestTemporaryDirectory();
        var snapshot = await CreateVerifiedSnapshotAsync(temporary, mode);

        var exception = await Assert.ThrowsAsync<DatabaseMaterializationException>(() => CreateMaterializer().MaterializeAsync(
            snapshot,
            new MaterializationOptions(temporary.GetPath("materialized")),
            CancellationToken.None));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(temporary.GetPath("materialized")));
    }

    [Fact]
    public async Task MaterializeAsync_redacts_sensitive_backend_error_output()
    {
        using var temporary = new TestTemporaryDirectory();
        var snapshot = await CreateVerifiedSnapshotAsync(temporary, "exit");

        var exception = await Assert.ThrowsAsync<DatabaseMaterializationException>(() => CreateMaterializer().MaterializeAsync(
            snapshot,
            new MaterializationOptions(temporary.GetPath("materialized")),
            CancellationToken.None));

        Assert.Equal(17, exception.ExitCode);
        Assert.DoesNotContain("00112233", exception.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("aabbccdd", exception.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\sensitive", exception.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<redacted>", exception.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaterializeAsync_rejects_backend_binary_hash_mismatch_before_launch()
    {
        using var temporary = new TestTemporaryDirectory();
        var snapshot = await CreateVerifiedSnapshotAsync(temporary, "success");
        var materializer = CreateMaterializer(new string('0', 64));

        var exception = await Assert.ThrowsAsync<DatabaseMaterializationException>(() => materializer.MaterializeAsync(
            snapshot,
            new MaterializationOptions(temporary.GetPath("materialized")),
            CancellationToken.None));

        Assert.Contains("binary hash", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(temporary.GetPath("materialized")));
    }

    [Fact]
    public async Task MaterializeAsync_kills_backend_and_fails_when_time_limit_expires()
    {
        using var temporary = new TestTemporaryDirectory();
        var snapshot = await CreateVerifiedSnapshotAsync(temporary, "hang");

        var exception = await Assert.ThrowsAsync<DatabaseMaterializationException>(() => CreateMaterializer().MaterializeAsync(
            snapshot,
            new MaterializationOptions(temporary.GetPath("materialized"), TimeSpan.FromMilliseconds(250)),
            CancellationToken.None));

        Assert.Contains("time limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(temporary.GetPath("materialized")));
    }

    [Fact]
    public async Task MaterializeAsync_kills_backend_and_preserves_cancellation_semantics()
    {
        using var temporary = new TestTemporaryDirectory();
        var snapshot = await CreateVerifiedSnapshotAsync(temporary, "hang");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateMaterializer().MaterializeAsync(
            snapshot,
            new MaterializationOptions(temporary.GetPath("materialized"), TimeSpan.FromSeconds(10)),
            cancellation.Token));

        Assert.False(Directory.Exists(temporary.GetPath("materialized")));
    }

    [Fact]
    public async Task MaterializeAsync_rejects_raw_snapshot_when_manifest_hash_is_wrong()
    {
        using var temporary = new TestTemporaryDirectory();
        var snapshotRoot = temporary.CreateDirectory("snapshot");
        var database = temporary.WriteFile(Path.Combine("snapshot", "message_0.db"), [1, 2, 3]);
        var manifest = new SnapshotManifest(
            snapshotRoot,
            snapshotRoot,
            DateTimeOffset.UtcNow,
            [new SnapshotFileRecord("message_0.db", new FileInfo(database).Length, new string('0', 64), DateTimeOffset.UtcNow)]);
        var exception = await Assert.ThrowsAsync<RawSnapshotVerificationException>(() => new RawSnapshotVerifier().VerifyAsync(
            new RawSnapshot(manifest),
            CancellationToken.None));

        Assert.Contains("failed manifest verification", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ExternalDatabaseMaterializer CreateMaterializer(string? expectedBinarySha256 = null)
    {
        var assemblyPath = typeof(FakeMaterializerMarker).Assembly.Location;
        var executablePath = Path.ChangeExtension(assemblyPath, ".exe");
        Assert.True(File.Exists(executablePath), $"Expected fake materializer apphost at '{executablePath}'.");
        return new ExternalDatabaseMaterializer(executablePath, expectedBinarySha256: expectedBinarySha256);
    }

    private static async Task<VerifiedRawSnapshot> CreateVerifiedSnapshotAsync(TestTemporaryDirectory temporary, string mode)
    {
        var snapshotRoot = temporary.CreateDirectory("snapshot");
        await SqliteSchemaInspectorTests.CreateSampleDatabaseAsync(Path.Combine(snapshotRoot, "message_0.db"));
        await SqliteSchemaInspectorTests.CreateSampleDatabaseAsync(Path.Combine(snapshotRoot, "media_0.db"));
        await SqliteSchemaInspectorTests.CreateSampleDatabaseAsync(Path.Combine(snapshotRoot, "contact.db"));
        await File.WriteAllTextAsync(Path.Combine(snapshotRoot, ".fake-materializer-mode"), mode);

        var records = new List<SnapshotFileRecord>();
        foreach (var path in Directory.EnumerateFiles(snapshotRoot).Order(StringComparer.OrdinalIgnoreCase))
        {
            var bytes = await File.ReadAllBytesAsync(path);
            records.Add(new SnapshotFileRecord(
                Path.GetFileName(path),
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                File.GetLastWriteTimeUtc(path)));
        }

        var manifest = new SnapshotManifest(snapshotRoot, snapshotRoot, DateTimeOffset.UtcNow, records);
        return await new RawSnapshotVerifier().VerifyAsync(new RawSnapshot(manifest), CancellationToken.None);
    }
}
