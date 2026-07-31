using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Materialization;

namespace WeChatVoice.KeyBroker;

/// <summary>
/// One-shot privileged host. It consumes exactly one bounded request and exits;
/// it is not a long-lived JSONL service and never returns key material.
/// </summary>
internal static class BrokerHost
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static async Task<int> RunAsync(
        TextReader input,
        TextWriter output,
        string snapshotManifestPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var line = input.ReadLine();
        if (line is null || line.Length > BrokerProtocol.MaximumRequestLength)
        {
            BrokerProtocol.Write(output, Failed(null, "request_too_large", "The one-shot broker request is missing or too large."));
            return 2;
        }

        BrokerRequest? request = null;
        try
        {
            request = BrokerProtocol.Parse(line);
            _ = await VerifySnapshotAsync(request, snapshotManifestPath, cancellationToken).ConfigureAwait(false);

            BrokerProtocol.Write(output, new BrokerResponse(
                "failed",
                request.RequestId,
                null,
                null,
                new BrokerError("profile_unavailable", "No verified Weixin key-extraction and database-encryption profile is installed.")));
            return 3;
        }
        catch (BrokerProtocolException exception)
        {
            BrokerProtocol.Write(output, Failed(exception.RequestId, exception.Code, exception.Message));
            return 2;
        }
        catch (JsonException)
        {
            BrokerProtocol.Write(output, Failed(request?.RequestId, "malformed_request", "The request is not valid JSON."));
            return 2;
        }
        catch (FileNotFoundException)
        {
            BrokerProtocol.Write(output, Failed(request?.RequestId, "snapshot_not_found", "The requested snapshot manifest was not found."));
            return 4;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            BrokerProtocol.Write(output, Failed(request?.RequestId, "snapshot_invalid", "The snapshot could not be verified."));
            return 4;
        }
    }

    private static async Task<VerifiedRawSnapshot> VerifySnapshotAsync(
        BrokerRequest request,
        string snapshotManifestPath,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.GetFullPath(snapshotManifestPath);
        if (!string.Equals(Path.GetFileName(manifestPath), "snapshot-manifest.json", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(Path.GetDirectoryName(manifestPath)), ".wechatvoice", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The broker accepts only a reserved snapshot manifest path.");
        }

        await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var manifest = await JsonSerializer.DeserializeAsync<SnapshotManifest>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The snapshot manifest was empty.");
        if (!string.Equals(manifest.SnapshotId, request.SnapshotId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The requested SnapshotId does not match the manifest.");
        }

        var metadataDirectory = Path.GetDirectoryName(manifestPath)!;
        var snapshotRoot = Directory.GetParent(metadataDirectory)?.FullName
            ?? throw new InvalidDataException("The snapshot manifest has no snapshot root.");
        return await new RawSnapshotVerifier().VerifyAsync(new RawSnapshot(manifest, snapshotRoot), cancellationToken).ConfigureAwait(false);
    }

    private static BrokerResponse Failed(string? requestId, string code, string message) =>
        new("failed", requestId, null, null, new BrokerError(code, message));
}
