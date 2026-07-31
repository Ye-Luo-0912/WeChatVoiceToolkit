using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Windows;

namespace WeChatVoice.ElevatedHelper;

/// <summary>
/// JSON Lines host for a deliberately narrow, read-only diagnostics protocol.
/// No request is ever interpreted as a shell command, a memory address, a key,
/// or a database-decryption instruction.
/// </summary>
internal static class HelperHost
{
    private const int MaximumRequestLength = 16 * 1024;
    private const int MaximumRequestIdLength = 128;

    private static readonly string[] SupportedOperations =
    [
        "ping",
        "capabilities",
        "list-wechat-processes",
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static int Run(TextReader input, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        while (TryReadLine(input, MaximumRequestLength, out var line, out var wasTooLong))
        {
            if (wasTooLong)
            {
                Write(output, ErrorResponse("request_too_large", "Request exceeds the maximum allowed line length."));
                continue;
            }

            Write(output, Handle(line!));
        }

        return 0;
    }

    private static HelperResponse Handle(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return ErrorResponse("malformed_request", "A JSON object with an operation is required.");
        }

        try
        {
            var request = ParseRequest(line);
            return request.Operation switch
            {
                "ping" => SuccessResponse(request, new PingResult("pong")),
                "capabilities" => SuccessResponse(request, CreateCapabilities()),
                "list-wechat-processes" => SuccessResponse(request, new ProcessListResult(WeChatProcessDiscovery.ListRunning())),
                _ => ErrorResponse("unknown_operation", "The requested operation is not supported.", request.RequestId),
            };
        }
        catch (ProtocolException exception)
        {
            return ErrorResponse(exception.Code, exception.Message, exception.RequestId);
        }
        catch (JsonException)
        {
            return ErrorResponse("malformed_request", "The request is not valid JSON.");
        }
        catch
        {
            // Do not expose exception details over the protocol boundary.
            return ErrorResponse("internal_error", "The request could not be processed.");
        }
    }

    private static HelperRequest ParseRequest(string line)
    {
        using var document = JsonDocument.Parse(line, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8,
        });

        if (document.RootElement.ValueKind is not JsonValueKind.Object)
        {
            throw new ProtocolException("malformed_request", "The request must be a JSON object.");
        }

        string? operation = null;
        string? requestId = null;
        var seenProperties = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!seenProperties.Add(property.Name))
            {
                throw new ProtocolException("malformed_request", "Duplicate request properties are not allowed.", requestId);
            }

            switch (property.Name)
            {
                case "operation":
                    if (property.Value.ValueKind is not JsonValueKind.String)
                    {
                        throw new ProtocolException("malformed_request", "The operation must be a string.", requestId);
                    }

                    operation = property.Value.GetString();
                    break;

                case "requestId":
                    if (property.Value.ValueKind is not JsonValueKind.String)
                    {
                        throw new ProtocolException("malformed_request", "The requestId must be a string.");
                    }

                    requestId = property.Value.GetString();
                    if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > MaximumRequestIdLength)
                    {
                        throw new ProtocolException("malformed_request", "The requestId is invalid.");
                    }

                    break;

                default:
                    throw new ProtocolException("malformed_request", "Only operation and requestId properties are allowed.", requestId);
            }
        }

        if (string.IsNullOrWhiteSpace(operation))
        {
            throw new ProtocolException("malformed_request", "An operation is required.", requestId);
        }

        if (operation.Length > 64)
        {
            throw new ProtocolException("malformed_request", "The operation is invalid.", requestId);
        }

        return new HelperRequest(operation, requestId);
    }

    private static CapabilitiesResult CreateCapabilities() => new(
        SupportedOperations,
        new SecurityBoundary(
            AllowsArbitraryCommands: false,
            AllowsProcessMemoryRead: false,
            AllowsKeyAccess: false,
            AllowsDatabaseDecryption: false));

    private static HelperResponse SuccessResponse(HelperRequest request, object result) =>
        new(true, request.Operation, request.RequestId, result, null);

    private static HelperResponse ErrorResponse(string code, string message, string? requestId = null) =>
        new(false, null, requestId, null, new ProtocolError(code, message));

    private static void Write(TextWriter output, HelperResponse response)
    {
        output.WriteLine(JsonSerializer.Serialize(response, SerializerOptions));
        output.Flush();
    }

    private static bool TryReadLine(TextReader input, int maximumLength, out string? line, out bool wasTooLong)
    {
        var builder = new StringBuilder(Math.Min(maximumLength, 256));
        wasTooLong = false;
        var sawAnyCharacter = false;

        while (true)
        {
            var next = input.Read();
            if (next < 0)
            {
                line = builder.ToString();
                return sawAnyCharacter || wasTooLong;
            }

            if (next is '\n')
            {
                line = builder.ToString();
                return true;
            }

            if (next is '\r')
            {
                continue;
            }

            sawAnyCharacter = true;
            if (builder.Length < maximumLength)
            {
                builder.Append((char)next);
            }
            else
            {
                wasTooLong = true;
            }
        }
    }

    private sealed record HelperRequest(string Operation, string? RequestId);

    private sealed record HelperResponse(
        bool Ok,
        string? Operation,
        string? RequestId,
        object? Result,
        ProtocolError? Error);

    private sealed record ProtocolError(string Code, string Message);

    private sealed record PingResult(string Message);

    private sealed record CapabilitiesResult(IReadOnlyList<string> Operations, SecurityBoundary Security);

    private sealed record SecurityBoundary(
        bool AllowsArbitraryCommands,
        bool AllowsProcessMemoryRead,
        bool AllowsKeyAccess,
        bool AllowsDatabaseDecryption);

    private sealed record ProcessListResult(IReadOnlyList<WeChatProcessInfo> Processes);

    private sealed class ProtocolException(string code, string message, string? requestId = null) : Exception(message)
    {
        internal string Code { get; } = code;

        internal string? RequestId { get; } = requestId;
    }
}
