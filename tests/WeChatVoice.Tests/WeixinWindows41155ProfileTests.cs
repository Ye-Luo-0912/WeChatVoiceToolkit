using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Sqlite;
using WeChatVoice.KeyAcquisition.Models;
using WeChatVoice.KeyAcquisition.Ports;
using WeChatVoice.KeyAcquisition.Validation;
using WeChatVoice.KeyBroker;
using WeChatVoice.Windows;

namespace WeChatVoice.Tests;

public sealed class WeixinWindows41155ProfileTests
{
    [Fact]
    public async Task Real_validator_profile_and_worker_complete_a_synthetic_database_chain()
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.CreateDirectory("snapshot");
        var encryptedPath = temporary.GetPath("snapshot", "message_0.db");
        var fixture = Path.Combine(AppContext.BaseDirectory, "WeChatVoice.SqlCipherFixture.dll");
        Assert.Equal(0, await RunDotnetAsync(fixture, ["--output", encryptedPath]));
        var bytes = await File.ReadAllBytesAsync(encryptedPath);
        var record = new SnapshotFileRecord(
            "message_0.db",
            bytes.LongLength,
            Hash(bytes),
            File.GetLastWriteTimeUtc(encryptedPath));
        var manifest = new SnapshotManifest(root, root, DateTimeOffset.UtcNow, [record]);
        var verified = new VerifiedRawSnapshot(new RawSnapshot(manifest, root), DateTimeOffset.UtcNow);
        var key = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        var process = new VerifiedWeixinProcess(
            42,
            DateTimeOffset.UnixEpoch,
            "C:\\Weixin.exe",
            WeixinWindows41155Profile.SupportedImageSha256,
            WeixinWindows41155Profile.SupportedVersion,
            "S-1-5-21-test",
            1,
            "x64");
        var profile = new WeixinWindows41155Profile(
            new WeixinWindows4SqlCipherKeyValidator(),
            new FakeMemorySourceFactory(Encoding.ASCII.GetBytes($"x'{Convert.ToHexString(key)}'")));

        var validated = await profile.AcquireAsync(
            process,
            verified,
            new KeyAcquisitionBudget(TimeSpan.FromSeconds(30), 64 * 1024 * 1024, 256),
            CancellationToken.None);
        using var acquisition = new VerifiedKeyAcquisition(
            "synthetic-acquisition",
            verified.SnapshotId,
            profile.Id,
            [new DatabaseKeyBinding(
                verified.SnapshotId,
                "S-1-5-21-test",
                validated[0].DatabaseGroupFingerprint,
                "message_0.db",
                0,
                profile.Id,
                profile.Descriptor.DatabaseEncryptionProfileId,
                validated[0].KeyMaterial)],
            DateTimeOffset.UtcNow);

        var output = temporary.GetPath("materialized");
        var worker = Path.Combine(AppContext.BaseDirectory, "WeChatVoice.SqlCipherWorker.dll");
        var materialized = await new SqlCipherEphemeralDatabaseMaterializer(worker).MaterializeAsync(
            verified,
            acquisition,
            new MaterializationOptions(output),
            CancellationToken.None);

        Assert.Single(materialized.Result.Databases);
        Assert.Equal("ok", await ReadQuickCheckAsync(Path.Combine(output, "databases", "message_0.db")));
    }

    [Fact]
    public async Task Profile_validates_one_candidate_against_every_database_group_and_returns_bound_buffers()
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.CreateDirectory("snapshot");
        var key = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        var pages = new Dictionary<string, byte[]>
        {
            ["message/message_0.db"] = BuildPage(key, 1),
            ["message/media_0.db"] = BuildPage(key, 2),
        };
        var files = new List<SnapshotFileRecord>();
        foreach (var pair in pages)
        {
            var path = temporary.WriteFile(Path.Combine("snapshot", pair.Key.Replace('/', Path.DirectorySeparatorChar)), pair.Value);
            files.Add(new SnapshotFileRecord(pair.Key, pair.Value.LongLength, Hash(pair.Value), File.GetLastWriteTimeUtc(path)));
        }

        var manifest = new SnapshotManifest(root, root, DateTimeOffset.UtcNow, files);
        var verified = new VerifiedRawSnapshot(new RawSnapshot(manifest, root), DateTimeOffset.UtcNow);
        var process = new VerifiedWeixinProcess(42, DateTimeOffset.UnixEpoch, "C:\\Weixin.exe", WeixinWindows41155Profile.SupportedImageSha256, WeixinWindows41155Profile.SupportedVersion, "S-1-5-21-test", 1, "x64");
        var profile = new WeixinWindows41155Profile(new FakeValidator(), new FakeMemorySourceFactory(Encoding.ASCII.GetBytes($"prefix x'{Convert.ToHexString(key)}' suffix")));

        var result = await profile.AcquireAsync(process, verified, new KeyAcquisitionBudget(TimeSpan.FromSeconds(30), 64 * 1024 * 1024, 256), CancellationToken.None);

        Assert.Equal(2, result.Count);
        foreach (var item in result)
        {
            using (item.KeyMaterial)
            {
                var observed = new byte[item.KeyMaterial.Length];
                item.KeyMaterial.CopyTo(observed);
                Assert.Equal(key, observed);
            }
        }
    }

    [Fact]
    public async Task Profile_rejects_partial_group_validation_and_clears_partial_keys()
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.CreateDirectory("snapshot");
        var page = new byte[4096];
        var path = temporary.WriteFile(Path.Combine("snapshot", "message", "message_0.db"), page);
        var file = new SnapshotFileRecord("message/message_0.db", page.LongLength, Hash(page), File.GetLastWriteTimeUtc(path));
        var manifest = new SnapshotManifest(root, root, DateTimeOffset.UtcNow, [file]);
        var verified = new VerifiedRawSnapshot(new RawSnapshot(manifest, root), DateTimeOffset.UtcNow);
        var process = new VerifiedWeixinProcess(42, DateTimeOffset.UnixEpoch, "C:\\Weixin.exe", WeixinWindows41155Profile.SupportedImageSha256, WeixinWindows41155Profile.SupportedVersion, "S-1-5-21-test", 1, "x64");
        var profile = new WeixinWindows41155Profile(new RejectingValidator(), new FakeMemorySourceFactory(Encoding.ASCII.GetBytes($"x'{new string('a', 64)}'")));

        await Assert.ThrowsAsync<InvalidDataException>(() => profile.AcquireAsync(process, verified, new KeyAcquisitionBudget(TimeSpan.FromSeconds(30), 64 * 1024 * 1024, 256), CancellationToken.None));
    }

    private static byte[] BuildPage(byte[] key, uint pageNumber)
    {
        var page = new byte[4096];
        RandomNumberGenerator.Fill(page.AsSpan(0, 16));
        var validator = new TestPageBuilder();
        validator.WriteHmac(page, key, pageNumber);
        return page;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task<int> RunDotnetAsync(string assembly, IReadOnlyList<string> arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
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

    private sealed class FakeValidator : IDatabaseKeyValidator
    {
        public string Id => "fake";

        public DatabaseKeyValidationResult ValidateFirstPage(ReadOnlySpan<byte> page, ReadOnlySpan<byte> candidate) =>
            candidate.Length == 32 && page.Length == 4096 && candidate[0] == 0 ? DatabaseKeyValidationResult.Valid : DatabaseKeyValidationResult.Invalid(DatabaseKeyValidationFailure.AuthenticationMismatch);
    }

    private sealed class RejectingValidator : IDatabaseKeyValidator
    {
        public string Id => "fake";
        public DatabaseKeyValidationResult ValidateFirstPage(ReadOnlySpan<byte> page, ReadOnlySpan<byte> candidate) => DatabaseKeyValidationResult.Invalid(DatabaseKeyValidationFailure.AuthenticationMismatch);
    }

    private sealed class FakeMemorySourceFactory(byte[] memory) : IWeixinProcessMemorySourceFactory
    {
        public IWeixinProcessMemorySource Open(VerifiedWeixinProcess process) => new FakeMemorySource(memory);
    }

    private sealed class FakeMemorySource(byte[] memory) : IWeixinProcessMemorySource
    {
        public ProcessMemoryScanResult Scan(ProcessMemoryChunkHandler handler, KeyAcquisitionBudget budget, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            handler(memory, true);
            return new ProcessMemoryScanResult(1, memory.LongLength, false);
        }

        public void Dispose() { }
    }

    private sealed class TestPageBuilder
    {
        public void WriteHmac(byte[] page, byte[] key, uint pageNumber) { }
    }
}
