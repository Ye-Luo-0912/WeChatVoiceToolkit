using System.Text.Json;
using WeChatVoice.Windows;

namespace WeChatVoice.Tests;

public sealed class ElevatedHelperProtocolTests
{
    [Fact]
    public async Task Public_helper_executable_accepts_only_its_safe_json_lines_protocol()
    {
        const string input = """
            {"operation":"ping","requestId":"ping-1"}
            {"operation":"capabilities","requestId":"capabilities-1"}
            {"operation":"list-wechat-processes","requestId":"processes-1"}
            {"operation":"not-supported","requestId":"unknown-1"}
            {"operation":"ping","requestId":"invalid-1","unexpected":"not-a-command"}
            """;

        var result = await ManagedProcessTestHarness.RunAssemblyAsync(
            "WeChatVoice.ElevatedHelper.dll",
            input);

        Assert.True(result.ExitCode == 0, result.StandardError);
        var responses = result.StandardOutput
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonDocument.Parse(line))
            .ToArray();
        try
        {
            Assert.Equal(5, responses.Length);

            Assert.True(responses[0].RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("ping", responses[0].RootElement.GetProperty("operation").GetString());
            Assert.Equal("ping-1", responses[0].RootElement.GetProperty("requestId").GetString());
            Assert.Equal("pong", responses[0].RootElement.GetProperty("result").GetProperty("message").GetString());

            Assert.True(responses[1].RootElement.GetProperty("ok").GetBoolean());
            var supportedOperations = responses[1].RootElement
                .GetProperty("result")
                .GetProperty("operations")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray();
            Assert.Equal(new[] { "ping", "capabilities", "list-wechat-processes" }, supportedOperations);
            var security = responses[1].RootElement.GetProperty("result").GetProperty("security");
            Assert.False(security.GetProperty("allowsArbitraryCommands").GetBoolean());
            Assert.False(security.GetProperty("allowsProcessMemoryRead").GetBoolean());
            Assert.False(security.GetProperty("allowsKeyAccess").GetBoolean());
            Assert.False(security.GetProperty("allowsDatabaseDecryption").GetBoolean());

            Assert.True(responses[2].RootElement.GetProperty("ok").GetBoolean());
            var processes = responses[2].RootElement.GetProperty("result").GetProperty("processes").EnumerateArray();
            foreach (var process in processes)
            {
                Assert.True(process.GetProperty("processId").GetInt32() > 0);
                Assert.Contains(
                    WeChatProcessDiscovery.SupportedProcessNames,
                    name => string.Equals(name, process.GetProperty("processName").GetString(), StringComparison.OrdinalIgnoreCase));
            }

            Assert.False(responses[3].RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("unknown-1", responses[3].RootElement.GetProperty("requestId").GetString());
            Assert.Equal("unknown_operation", responses[3].RootElement.GetProperty("error").GetProperty("code").GetString());

            Assert.False(responses[4].RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("invalid-1", responses[4].RootElement.GetProperty("requestId").GetString());
            Assert.Equal("malformed_request", responses[4].RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }
}
