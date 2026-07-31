using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WeChatVoice.Core.Models;
using WeChatVoice.KeyBroker;

namespace WeChatVoice.Tests;

public sealed class KeyBrokerProtocolTests
{
    [Fact]
    public async Task Broker_accepts_only_fixed_request_shape_and_fails_closed_without_profile()
    {
        using var temporary = new TestTemporaryDirectory();
        var (snapshotId, manifestPath) = await CreateSnapshotAsync(temporary);
        var input = JsonSerializer.Serialize(new
        {
            protocolVersion = 1,
            requestId = "request-1",
            snapshotId,
            operation = "acquire-and-materialize",
        }) + Environment.NewLine;
        var output = new StringWriter();
        var exitCode = await BrokerHost.RunAsync(new StringReader(input), output, manifestPath, CancellationToken.None);

        Assert.Equal(3, exitCode);
        using var response = JsonDocument.Parse(output.ToString());
        Assert.Equal("failed", response.RootElement.GetProperty("status").GetString());
        Assert.Equal("request-1", response.RootElement.GetProperty("requestId").GetString());
        Assert.Equal("profile_unavailable", response.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(response.RootElement.TryGetProperty("key", out _));
    }

    [Fact]
    public async Task Broker_rejects_memory_reader_fields()
    {
        var forbiddenFields = new[] { "pid", "address", "length", "processName", "command" };
        foreach (var field in forbiddenFields)
        {
            var input = $"{{\"protocolVersion\":1,\"requestId\":\"request-2\",\"snapshotId\":\"{new string('a', 64)}\",\"operation\":\"acquire-and-materialize\",\"{field}\":1234}}\n";
            var output = new StringWriter();
            var exitCode = await BrokerHost.RunAsync(new StringReader(input), output, "C:\\snapshot\\.wechatvoice\\snapshot-manifest.json", CancellationToken.None);

            Assert.Equal(2, exitCode);
            using var response = JsonDocument.Parse(output.ToString());
            Assert.Equal("failed", response.RootElement.GetProperty("status").GetString());
            Assert.Equal("malformed_request", response.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Broker_rejects_snapshot_id_mismatch_before_profile_selection()
    {
        using var temporary = new TestTemporaryDirectory();
        var (_, manifestPath) = await CreateSnapshotAsync(temporary);
        var input = JsonSerializer.Serialize(new
        {
            protocolVersion = 1,
            requestId = "request-3",
            snapshotId = new string('0', 64),
            operation = "acquire-and-materialize",
        }) + Environment.NewLine;

        var output = new StringWriter();
        var exitCode = await BrokerHost.RunAsync(new StringReader(input), output, manifestPath, CancellationToken.None);

        Assert.Equal(4, exitCode);
        using var response = JsonDocument.Parse(output.ToString());
        Assert.Equal("snapshot_invalid", response.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Broker_snapshot_staging_rejects_live_source_and_isolates_verified_content()
    {
        using var temporary = new TestTemporaryDirectory();
        var snapshotRoot = temporary.CreateDirectory("snapshot");
        var sourcePath = temporary.WriteFile(Path.Combine("snapshot", "evidence.bin"), [1, 2, 3]);
        var original = await File.ReadAllBytesAsync(sourcePath);
        var record = new SnapshotFileRecord(
            "evidence.bin",
            original.LongLength,
            Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant(),
            File.GetLastWriteTimeUtc(sourcePath));
        var manifest = new SnapshotManifest(snapshotRoot, snapshotRoot, DateTimeOffset.UtcNow, [record]);
        var verified = new VerifiedRawSnapshot(new RawSnapshot(manifest, snapshotRoot), DateTimeOffset.UtcNow);

        await using (var staged = await BrokerSnapshotStager.StageAsync(verified, temporary.GetPath("staging"), CancellationToken.None))
        {
            await File.WriteAllBytesAsync(sourcePath, [9, 9, 9]);
            Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(staged.Snapshot.Snapshot.SnapshotDirectory, "evidence.bin")));
        }

        var liveManifest = new SnapshotManifest(snapshotRoot, snapshotRoot, DateTimeOffset.UtcNow, [record], PotentiallyInconsistent: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => BrokerSnapshotStager.StageAsync(
            new VerifiedRawSnapshot(new RawSnapshot(liveManifest, snapshotRoot), DateTimeOffset.UtcNow),
            temporary.GetPath("staging-live"),
            CancellationToken.None));
    }

    [Fact]
    public async Task One_shot_pipe_uses_random_transport_token_and_returns_no_key_material()
    {
        using var temporary = new TestTemporaryDirectory();
        var (snapshotId, manifestPath) = await CreateSnapshotAsync(temporary);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = BrokerPipeServer.RunAsync(
            token,
            manifestPath,
            temporary.GetPath("materialized"),
            temporary.GetPath("materialized", ".wechatvoice", "local-workspace.json"),
            timeout.Token);

        await using var client = new NamedPipeClientStream(
            ".",
            BrokerPipeServer.PipePrefix + token,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(timeout.Token);
        await using var writer = new StreamWriter(client, new UTF8Encoding(false, true), 4096, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(new
        {
            protocolVersion = 1,
            requestId = "request-pipe",
            snapshotId,
            operation = "acquire-and-materialize",
        }));
        string? responseLine;
        do
        {
            responseLine = await reader.ReadLineAsync(timeout.Token);
        }
        while (responseLine is not null && responseLine.Contains("\"stage\"", StringComparison.Ordinal));

        Assert.Equal(3, await serverTask);
        Assert.NotNull(responseLine);
        using var response = JsonDocument.Parse(responseLine);
        Assert.False(response.RootElement.TryGetProperty("key", out _));
        Assert.Equal("profile_unavailable", response.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static async Task<(string SnapshotId, string ManifestPath)> CreateSnapshotAsync(TestTemporaryDirectory temporary)
    {
        var snapshotRoot = temporary.CreateDirectory("snapshot");
        var dataPath = temporary.WriteFile(Path.Combine("snapshot", "evidence.bin"), [1, 2, 3, 4]);
        var bytes = await File.ReadAllBytesAsync(dataPath);
        var manifest = new SnapshotManifest(
            snapshotRoot,
            snapshotRoot,
            DateTimeOffset.UtcNow,
            [new SnapshotFileRecord("evidence.bin", bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), File.GetLastWriteTimeUtc(dataPath))]);
        var metadataDirectory = temporary.CreateDirectory("snapshot", ".wechatvoice");
        var manifestPath = Path.Combine(metadataDirectory, "snapshot-manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return (manifest.SnapshotId, manifestPath);
    }
}
