using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Protocol;
using WeChatVoice.Infrastructure.Materialization;
using WeChatVoice.Infrastructure.Sqlite;
using WeChatVoice.KeyAcquisition;
using WeChatVoice.KeyAcquisition.Models;
using WeChatVoice.Windows;

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
        => await RunAsync(input, output, snapshotManifestPath, null, null, cancellationToken, false, null).ConfigureAwait(false);

    internal static async Task<int> RunAsync(
        TextReader input,
        TextWriter output,
        string snapshotManifestPath,
        string? outputRoot,
        string? workspaceOutput,
        CancellationToken cancellationToken,
        bool allowExperimentalProfile = false,
        Action<BrokerStageEvent>? reportStage = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        string? line;
        try
        {
            line = await BoundedLineReader.ReadAsync(input, BrokerProtocol.MaximumRequestLength, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            BrokerProtocol.Write(output, Failed(null, "request_too_large", "The one-shot broker request is missing or too large."));
            return 2;
        }

        if (line is null)
        {
            BrokerProtocol.Write(output, Failed(null, "request_too_large", "The one-shot broker request is missing or too large."));
            return 2;
        }

        BrokerRequest? request = null;
        var snapshotVerified = false;
        var snapshotStaged = false;
        try
        {
            request = BrokerProtocol.Parse(line);
            var verifiedSnapshot = await VerifySnapshotAsync(request, snapshotManifestPath, cancellationToken).ConfigureAwait(false);
            snapshotVerified = true;
            if (string.IsNullOrWhiteSpace(outputRoot) || string.IsNullOrWhiteSpace(workspaceOutput))
            {
                BrokerProtocol.Write(output, new BrokerResponse(
                    "failed",
                    request.RequestId,
                    null,
                    null,
                    new BrokerError("profile_unavailable", "No materialization output was supplied to the one-shot Broker.")));
                return 3;
            }

            await using var stagedSnapshot = await BrokerSnapshotStager.StageAsync(
                verifiedSnapshot,
                Path.GetDirectoryName(Path.GetFullPath(outputRoot))
                    ?? throw new InvalidDataException("The materialization output has no staging parent."),
                cancellationToken).ConfigureAwait(false);
            verifiedSnapshot = stagedSnapshot.Snapshot;
            snapshotStaged = true;
            var profile = GuardedKeyExtractionProfiles.Create(
                scan => reportStage?.Invoke(new BrokerStageEvent("memory-scan", scan.ScannedBytes, scan.CandidateCount))).Single();
            var materializer = new SqlCipherEphemeralDatabaseMaterializer(
                progress: (completed, total) => reportStage?.Invoke(new BrokerStageEvent(
                    "materializing",
                    CompletedDatabases: completed,
                    TotalDatabases: total)));
            var service = new EphemeralAcquireAndMaterializeService(
                new ProfileDrivenKeyAcquisitionService(
                    new WeixinProcessLocator(),
                    new WindowsWeixinProcessIdentityReader(),
                    [profile],
                    reportStage),
                materializer);
            var materialization = await service.ExecuteAsync(
                verifiedSnapshot,
                new KeyAcquisitionOptions(profile.Id, TimeSpan.FromSeconds(60), 768L * 1024 * 1024, 256, allowExperimentalProfile),
                new MaterializationOptions(Path.GetFullPath(outputRoot)),
                cancellationToken).ConfigureAwait(false);
            var workspace = await new LocalWorkspaceCreator().CreateAsync(materialization, cancellationToken).ConfigureAwait(false);
            var workspacePath = Path.GetFullPath(workspaceOutput);
            Directory.CreateDirectory(Path.GetDirectoryName(workspacePath)!);
            await using (var workspaceStream = new FileStream(workspacePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(workspaceStream, workspace, JsonOptions, cancellationToken).ConfigureAwait(false);
                await workspaceStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            BrokerProtocol.Write(output, new BrokerResponse(
                "completed",
                request.RequestId,
                profile.Id,
                materialization.Result.WorkspaceId,
                null));
            return 0;
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
            var noProfile = exception is InvalidDataException;
            if (!snapshotVerified || !snapshotStaged)
            {
                BrokerProtocol.Write(output, Failed(request?.RequestId, "snapshot_invalid", "The snapshot could not be verified."));
                return 4;
            }

            BrokerProtocol.Write(output, Failed(
                request?.RequestId,
                noProfile ? "profile_unavailable" : "materialization_failed",
                noProfile ? "No running Weixin process matched the Profile, or no candidate key validated every database group." : "The Broker could not complete the verified materialization."));
            return noProfile ? 3 : 1;
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
