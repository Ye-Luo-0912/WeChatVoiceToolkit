using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Core.Models;

namespace WeChatVoice.KeyBroker;

/// <summary>
/// Fixed pipe request protocol. Transport bootstrap owns the random pipe token
/// and verified Snapshot Manifest path; neither is accepted as a JSON field.
/// PID, process name, address, length, module base, database path, decryptor
/// executable, and arbitrary command fields are deliberately absent.
/// </summary>
internal static class BrokerProtocol
{
    internal const int MaximumRequestLength = 16 * 1024;
    internal const int MaximumResponseLength = 16 * 1024;
    private const int SupportedProtocolVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static BrokerRequest Parse(string line)
    {
        using var document = JsonDocument.Parse(line, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new BrokerProtocolException("malformed_request", "The request must be a JSON object.");
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "protocolVersion", "requestId", "snapshotId", "operation",
        };
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !values.TryAdd(property.Name, property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null))
            {
                throw new BrokerProtocolException("malformed_request", "Only the fixed broker request fields are allowed.", values.GetValueOrDefault("requestId"));
            }
        }

        if (!document.RootElement.TryGetProperty("protocolVersion", out var versionElement)
            || versionElement.ValueKind != JsonValueKind.Number
            || versionElement.GetInt32() != SupportedProtocolVersion)
        {
            throw new BrokerProtocolException("unsupported_protocol", "The broker protocol version is not supported.", values.GetValueOrDefault("requestId"));
        }

        var requestId = Required(values, "requestId");
        var snapshotId = Required(values, "snapshotId");
        var operation = values.GetValueOrDefault("operation") ?? "acquire-and-materialize";
        if (!string.Equals(operation, "acquire-and-materialize", StringComparison.Ordinal))
        {
            throw new BrokerProtocolException("unsupported_operation", "Only acquire-and-materialize is supported.", requestId);
        }

        if (requestId.Length > 128 || snapshotId.Length != 64 || !snapshotId.All(Uri.IsHexDigit))
        {
            throw new BrokerProtocolException("malformed_request", "The broker request contains an invalid identifier.", requestId);
        }

        return new BrokerRequest(requestId, snapshotId.ToLowerInvariant(), operation);
    }

    internal static void Write(TextWriter output, BrokerResponse response)
    {
        output.WriteLine(JsonSerializer.Serialize(response, SerializerOptions));
        output.Flush();
    }

    internal static void Write(TextWriter output, BrokerStageEvent stage)
    {
        output.WriteLine(JsonSerializer.Serialize(stage, SerializerOptions));
        output.Flush();
    }

    private static string Required(IReadOnlyDictionary<string, string?> values, string name)
        => !values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value)
            ? throw new BrokerProtocolException("malformed_request", $"The broker field '{name}' is required.", values.GetValueOrDefault("requestId"))
            : value;
}

internal sealed record BrokerRequest(string RequestId, string SnapshotId, string Operation);

internal sealed class BrokerProtocolException(string code, string message, string? requestId = null) : Exception(message)
{
    internal string Code { get; } = code;

    internal string? RequestId { get; } = requestId;
}
