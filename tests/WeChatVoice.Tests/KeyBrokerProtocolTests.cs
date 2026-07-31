using System.Security.Cryptography;
using System.Text.Json;
using WeChatVoice.Core.Models;

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
            nonce = "nonce-1",
            snapshotId,
            snapshotManifestPath = manifestPath,
            operation = "acquire-and-materialize",
        }) + Environment.NewLine;
        var result = await ManagedProcessTestHarness.RunAssemblyAsync("WeChatVoice.KeyBroker.dll", input);

        Assert.Equal(3, result.ExitCode);
        using var response = JsonDocument.Parse(result.StandardOutput);
        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("request-1", response.RootElement.GetProperty("requestId").GetString());
        Assert.Equal("profile_unavailable", response.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Broker_rejects_memory_reader_fields()
    {
        var input = "{\"protocolVersion\":1,\"requestId\":\"request-2\",\"nonce\":\"nonce-2\",\"snapshotId\":\"snapshot-2\",\"snapshotManifestPath\":\"C:\\\\snapshot\\\\manifest.json\",\"operation\":\"acquire-and-materialize\",\"pid\":1234}\n";
        var result = await ManagedProcessTestHarness.RunAssemblyAsync("WeChatVoice.KeyBroker.dll", input);

        Assert.Equal(2, result.ExitCode);
        using var response = JsonDocument.Parse(result.StandardOutput);
        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("malformed_request", response.RootElement.GetProperty("error").GetProperty("code").GetString());
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
            nonce = "nonce-3",
            snapshotId = new string('0', 64),
            snapshotManifestPath = manifestPath,
            operation = "acquire-and-materialize",
        }) + Environment.NewLine;

        var result = await ManagedProcessTestHarness.RunAssemblyAsync("WeChatVoice.KeyBroker.dll", input);

        Assert.Equal(4, result.ExitCode);
        using var response = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("snapshot_invalid", response.RootElement.GetProperty("error").GetProperty("code").GetString());
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
