using System.Security.Cryptography;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Materialization;
using WeChatVoice.Infrastructure.Sqlite;
using WeChatVoice.KeyAcquisition.Models;
using WeChatVoice.KeyAcquisition.Validation;
using WeChatVoice.KeyBroker;
using WeChatVoice.Windows;

namespace WeChatVoice.Tests;

public sealed class SqlCipherMaterializerTests
{
    [Fact]
    public async Task Materializer_runs_the_worker_and_returns_verified_plaintext_workspace()
    {
        using var temporary = new TestTemporaryDirectory();
        var snapshotRoot = temporary.CreateDirectory("snapshot");
        var encrypted = Path.Combine(snapshotRoot, "message_0.db");
        var optionalMigration = Path.Combine(snapshotRoot, "migrate", "unspportmsg.db");
        var fixture = Path.Combine(AppContext.BaseDirectory, "WeChatVoice.SqlCipherFixture.dll");
        var worker = Path.Combine(AppContext.BaseDirectory, "WeChatVoice.SqlCipherWorker.exe");
        Assert.Equal(0, await RunDotnetAsync(fixture, ["--output", encrypted]));
        Directory.CreateDirectory(Path.GetDirectoryName(optionalMigration)!);
        await File.WriteAllBytesAsync(optionalMigration, Enumerable.Repeat((byte)0xA5, 4096).ToArray());

        var bytes = await File.ReadAllBytesAsync(encrypted);
        var optionalBytes = await File.ReadAllBytesAsync(optionalMigration);
        var manifest = new SnapshotManifest(
            snapshotRoot,
            snapshotRoot,
            DateTimeOffset.UtcNow,
            [
                new SnapshotFileRecord("message_0.db", bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), File.GetLastWriteTimeUtc(encrypted)),
                new SnapshotFileRecord("migrate/unspportmsg.db", optionalBytes.LongLength, Convert.ToHexString(SHA256.HashData(optionalBytes)).ToLowerInvariant(), File.GetLastWriteTimeUtc(optionalMigration)),
            ]);
        var verifiedSnapshot = await new RawSnapshotVerifier().VerifyAsync(new RawSnapshot(manifest), CancellationToken.None);
        var probe = await new DataSetProbeService().ProbeAsync(snapshotRoot, new DataSetProbeOptions(IncludeLocalPaths: true), CancellationToken.None);
        var artifact = Assert.Single(probe.DataSet.Databases, static database => database.DatabasePath == "message_0.db");
        var key = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        using var protectedKey = new SensitiveBuffer(key);
        CryptographicOperations.ZeroMemory(key);
        using var acquisition = new VerifiedKeyAcquisition(
            "acquisition-test",
            verifiedSnapshot.Snapshot.SnapshotId,
            "weixin-windows-4.1.11.55-wcdb-protected-spec-v2",
            [new DatabaseKeyBinding(
                verifiedSnapshot.Snapshot.SnapshotId,
                "S-1-5-21-test",
                artifact.DatabaseGroupFingerprint ?? throw new InvalidDataException("The test artifact did not have a group fingerprint."),
                artifact.DatabasePath,
                artifact.ShardNumber,
                "weixin-windows-4.1.11.55-wcdb-protected-spec-v2",
                WeixinWindows4SqlCipherKeyValidator.EncryptionProfileId,
                protectedKey)],
            DateTimeOffset.UtcNow);

        var output = temporary.GetPath("materialized");
        var result = await new SqlCipherEphemeralDatabaseMaterializer(worker).MaterializeAsync(
            verifiedSnapshot,
            acquisition,
            new MaterializationOptions(output),
            CancellationToken.None);

        Assert.Equal(verifiedSnapshot.Snapshot.SnapshotId, result.Result.SourceSnapshotId);
        Assert.True(File.Exists(Path.Combine(output, "databases", "message_0.db")));
        Assert.True(File.Exists(result.Result.ManifestPath));
        Assert.Equal("ok", await ReadQuickCheckAsync(Path.Combine(output, "databases", "message_0.db")));
        var ignored = Assert.Single(result.Result.Databases, static database => database.Status == MaterializationDatabaseStatus.IntentionallyIgnored);
        Assert.Equal("migrate/unspportmsg.db", ignored.SourceRelativePath);
        Assert.Empty(ignored.OutputRelativePath);
    }

    private static async Task<int> RunDotnetAsync(string assembly, IReadOnlyList<string> arguments)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add(assembly);
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        Assert.True(process.Start());
        _ = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        return process.ExitCode;
    }

    private static async Task<string?> ReadQuickCheckAsync(string path)
    {
        WindowsSqliteProvider.EnsureInitialized();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
