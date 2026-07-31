using System.Text.Json;

namespace WeChatVoice.Tests;

public sealed class KeyBrokerProtocolTests
{
    [Fact]
    public async Task Broker_accepts_only_fixed_request_shape_and_fails_closed_without_profile()
    {
        var input = "{\"protocolVersion\":1,\"requestId\":\"request-1\",\"nonce\":\"nonce-1\",\"snapshotId\":\"snapshot-1\",\"snapshotManifestPath\":\"C:\\\\snapshot\\\\.wechatvoice\\\\snapshot-manifest.json\",\"operation\":\"acquire-and-materialize\"}\n";
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
}
