using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeChatVoice.KeyBroker;

/// <summary>
/// One-shot privileged boundary for a future verified Weixin profile. This
/// executable deliberately does not expose a JSONL service, arbitrary process
/// handles, memory addresses, key material, or decryptor commands. Until a
/// version-specific profile and database validation suite are supplied, it
/// fails closed with a structured error.
/// </summary>
internal static class Program
{
    private const int MaximumRequestLength = 16 * 1024;
    private const int SupportedProtocolVersion = 1;

    private static int Main()
    {
        var line = Console.In.ReadLine();
        if (line is null || line.Length > MaximumRequestLength)
        {
            Write(new BrokerResponse(false, null, null, new BrokerError("request_too_large", "The one-shot broker request is missing or too large.")));
            return 2;
        }

        try
        {
            var request = Parse(line);
            Write(new BrokerResponse(
                false,
                request.RequestId,
                null,
                new BrokerError("profile_unavailable", "No verified Weixin key-extraction and database-encryption profile is installed.")));
            return 3;
        }
        catch (BrokerProtocolException exception)
        {
            Write(new BrokerResponse(false, exception.RequestId, null, new BrokerError(exception.Code, exception.Message)));
            return 2;
        }
        catch (JsonException)
        {
            Write(new BrokerResponse(false, null, null, new BrokerError("malformed_request", "The request is not valid JSON.")));
            return 2;
        }
    }

    private static BrokerRequest Parse(string line)
    {
        using var document = JsonDocument.Parse(line, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new BrokerProtocolException("malformed_request", "The request must be a JSON object.");
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "protocolVersion", "requestId", "nonce", "snapshotId", "snapshotManifestPath", "operation",
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
        var nonce = Required(values, "nonce");
        var snapshotId = Required(values, "snapshotId");
        var manifestPath = Required(values, "snapshotManifestPath");
        var operation = values.GetValueOrDefault("operation") ?? "acquire-and-materialize";
        if (!string.Equals(operation, "acquire-and-materialize", StringComparison.Ordinal))
        {
            throw new BrokerProtocolException("unsupported_operation", "Only acquire-and-materialize is supported.", requestId);
        }

        if (nonce.Length > 128 || requestId.Length > 128 || snapshotId.Length > 128 || !Path.IsPathFullyQualified(manifestPath))
        {
            throw new BrokerProtocolException("malformed_request", "The broker request contains an invalid identifier or manifest path.", requestId);
        }

        return new BrokerRequest(requestId);
    }

    private static string Required(IReadOnlyDictionary<string, string?> values, string name)
        => !values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value)
            ? throw new BrokerProtocolException("malformed_request", $"The broker field '{name}' is required.", values.GetValueOrDefault("requestId"))
            : value;

    private static void Write(BrokerResponse response)
        => Console.WriteLine(JsonSerializer.Serialize(response, SerializerOptions));

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record BrokerRequest(string RequestId);
    private sealed record BrokerResponse(bool Ok, string? RequestId, object? Result, BrokerError? Error);
    private sealed record BrokerError(string Code, string Message);

    private sealed class BrokerProtocolException(string code, string message, string? requestId = null) : Exception(message)
    {
        internal string Code { get; } = code;
        internal string? RequestId { get; } = requestId;
    }
}
