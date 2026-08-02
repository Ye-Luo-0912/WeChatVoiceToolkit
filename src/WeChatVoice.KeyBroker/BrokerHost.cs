using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Core.Errors;
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
        Action<BrokerStageEvent>? reportStage = null,
        string? callerSid = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        string? line;
        try
        {
            using var framedInput = new BoundedLineReader(input, BrokerProtocol.MaximumRequestLength);
            line = await framedInput.ReadAsync(cancellationToken).ConfigureAwait(false);
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
        MaterializationStateLock? materializationLock = null;
        var operationId = Guid.NewGuid().ToString("N");
        try
        {
            request = BrokerProtocol.Parse(line);
            var verifiedSnapshot = await VerifySnapshotAsync(request, snapshotManifestPath, cancellationToken).ConfigureAwait(false);
            snapshotVerified = true;
            if (string.IsNullOrWhiteSpace(outputRoot) || string.IsNullOrWhiteSpace(workspaceOutput))
            {
                var hostError = ErrorCatalog.Get(ErrorCode.MaterializationInvalid);
                BrokerProtocol.Write(output, new BrokerResponse(
                    "failed",
                    request.RequestId,
                    null,
                    null,
                    new BrokerError(
                        BrokerErrorKind.Domain,
                        ErrorCode.MaterializationInvalid.ToString(),
                        "No materialization output was supplied to the one-shot Broker.",
                        hostError.IsRetryable,
                        hostError.SuggestedAction,
                        hostError.NonSensitiveTechnicalContext)));
                return 3;
            }

            var snapshotRoot = Path.GetDirectoryName(Path.GetFullPath(snapshotManifestPath)) is { } metadata
                ? Directory.GetParent(metadata)?.FullName
                : null;
            if (snapshotRoot is null)
            {
                throw new InvalidDataException("The snapshot root could not be determined.");
            }

            PathOverlapGuard.EnsureDisjoint(snapshotRoot, outputRoot, Path.GetFullPath(workspaceOutput));

            await using var stagedSnapshot = await BrokerSnapshotStager.StageAsync(
                verifiedSnapshot,
                Path.GetDirectoryName(Path.GetFullPath(outputRoot))
                    ?? throw new InvalidDataException("The materialization output has no staging parent."),
                cancellationToken).ConfigureAwait(false);
            verifiedSnapshot = stagedSnapshot.Snapshot;
            snapshotStaged = true;
            reportStage?.Invoke(new BrokerStageEvent("snapshot-staged"));
            var profile = GuardedKeyExtractionProfiles.Create(
                scan =>
                {
                    reportStage?.Invoke(new BrokerStageEvent(
                        "memory-scan",
                        scan.Memory.ScannedBytes,
                        scan.Memory.CandidateCount));
                    reportStage?.Invoke(new BrokerStageEvent(
                        "key-validation",
                        CompletedGroups: scan.ValidatedGroups,
                        TotalGroups: scan.TotalGroups,
                        FirstUnvalidatedGroupOrdinal: scan.FirstUnvalidatedGroupOrdinal));
                }).Single();
            reportStage?.Invoke(new BrokerStageEvent("profile-selected"));
            var materializer = new SqlCipherEphemeralDatabaseMaterializer(
                progress: (completed, total) => reportStage?.Invoke(new BrokerStageEvent(
                    "materializing",
                    CompletedDatabases: completed,
                    TotalDatabases: total)),
                checkpoint: stage => reportStage?.Invoke(new BrokerStageEvent(stage)),
                finalWorkspaceUserSid: callerSid);
            var service = new EphemeralAcquireAndMaterializeService(
                new ProfileDrivenKeyAcquisitionService(
                    new WeixinProcessLocator(),
                    new WindowsWeixinProcessIdentityReader(),
                    [profile],
                    reportStage),
                materializer);
            var materialization = await service.ExecuteAsync(
                verifiedSnapshot,
                new KeyAcquisitionOptions(profile.Id, TimeSpan.FromSeconds(60), 1024L * 1024 * 1024, 256, allowExperimentalProfile),
                new MaterializationOptions(Path.GetFullPath(outputRoot)),
                cancellationToken).ConfigureAwait(false);
            materializationLock = await MaterializationStateStore.AcquireLockAsync(outputRoot, cancellationToken).ConfigureAwait(false);
            var sourceIdentity = SnapshotSourceIdentity.TryDerive(
                verifiedSnapshot.Snapshot.Manifest.SourceDirectory,
                verifiedSnapshot.Snapshot.Manifest.Files);
            var accountId = sourceIdentity?.AccountCandidate;
            LocalWorkspace workspace;
            try
            {
                workspace = await new LocalWorkspaceCreator().CreateAsync(
                    materialization,
                    accountId,
                    sourceIdentity,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AppFailureException)
            {
                throw;
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                throw new AppFailureException(ErrorCode.WorkspaceInvalid, "The Broker could not create a verified local workspace.", exception);
            }
            var workspacePath = Path.GetFullPath(workspaceOutput);
            Directory.CreateDirectory(Path.GetDirectoryName(workspacePath)!);
            await LocalWorkspaceDocumentStore.WriteAsync(workspacePath, workspace, cancellationToken).ConfigureAwait(false);
            await MaterializationStateStore.TransitionAsync(
                outputRoot,
                [MaterializationCommitStates.DatabasesCommitted, MaterializationCommitStates.FailedRecoverable],
                MaterializationCommitStates.WorkspaceCommitted,
                operationId,
                failureCode: null,
                cancellationToken: cancellationToken,
                heldLock: materializationLock!).ConfigureAwait(false);
            await MaterializationStateStore.TransitionAsync(
                outputRoot,
                [MaterializationCommitStates.WorkspaceCommitted],
                MaterializationCommitStates.Completed,
                operationId,
                failureCode: null,
                cancellationToken: cancellationToken,
                heldLock: materializationLock!).ConfigureAwait(false);

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
        catch (AppFailureException exception)
        {
            await TryMarkRecoverableMaterializationAsync(outputRoot, operationId, materializationLock).ConfigureAwait(false);
            return Fail(request, exception.Code, exception.Message, snapshotVerified, snapshotStaged, reportStage, output);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Untyped validation/IO failures before the snapshot boundary are
            // snapshot verification failures; afterwards they are materialization
            // failures. AppFailureException sites carry their own precise code.
            var code = !snapshotVerified || !snapshotStaged
                ? ErrorCode.SnapshotInvalid
                : ErrorCode.MaterializationInvalid;
            await TryMarkRecoverableMaterializationAsync(outputRoot, operationId, materializationLock).ConfigureAwait(false);
            return Fail(request, code, null, snapshotVerified, snapshotStaged, reportStage, output);
        }
        catch (OperationCanceledException)
        {
            await TryMarkRecoverableMaterializationAsync(outputRoot, operationId, materializationLock).ConfigureAwait(false);
            reportStage?.Invoke(new BrokerStageEvent("operation-cancelled"));
            BrokerProtocol.Write(output, Failed(request?.RequestId, "cancelled", "The one-shot Broker operation was cancelled."));
            return 130;
        }
        catch (Exception)
        {
            // A one-shot elevated process must still return a bounded,
            // non-sensitive terminal response for unexpected runtime errors.
            // Exception messages and stack traces never cross the pipe.
            await TryMarkRecoverableMaterializationAsync(outputRoot, operationId, materializationLock).ConfigureAwait(false);
            reportStage?.Invoke(new BrokerStageEvent("operation-failed-runtime"));
            BrokerProtocol.Write(output, Failed(request?.RequestId, "broker_internal", "The Key Broker encountered an internal runtime failure."));
            return 1;
        }
        finally
        {
            if (materializationLock is not null)
            {
                await materializationLock.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task TryMarkRecoverableMaterializationAsync(
        string? outputRoot,
        string operationId,
        MaterializationStateLock? heldLock)
    {
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            return;
        }

        try
        {
            var fullRoot = Path.GetFullPath(outputRoot);
            if (!Directory.Exists(fullRoot) || !File.Exists(MaterializationStateStore.GetPath(fullRoot)))
            {
                return;
            }

            if (heldLock is not null)
            {
                await MaterializationStateStore.TryTransitionToFailedRecoverableAsync(
                    fullRoot,
                    operationId,
                    ErrorCode.MaterializationInvalid.ToString(),
                    CancellationToken.None,
                    heldLock).ConfigureAwait(false);
            }
            else
            {
                await MaterializationStateStore.TryTransitionToFailedRecoverableAsync(
                    fullRoot,
                    operationId,
                    ErrorCode.MaterializationInvalid.ToString(),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Recovery is best effort. The original typed Broker failure must
            // remain the terminal result when the marker itself is unwritable.
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
            throw new AppFailureException(ErrorCode.SnapshotInvalid, "The broker accepts only a reserved snapshot manifest path.");
        }

        FileStream stream;
        try
        {
            stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException exception)
        {
            throw new AppFailureException(ErrorCode.SnapshotInvalid, "The snapshot manifest was not found.", exception);
        }
        await using (stream)
        {
            var manifest = await JsonSerializer.DeserializeAsync<SnapshotManifest>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new AppFailureException(ErrorCode.SnapshotInvalid, "The snapshot manifest was empty.");
            if (!string.Equals(manifest.SnapshotId, request.SnapshotId, StringComparison.OrdinalIgnoreCase))
            {
                throw new AppFailureException(ErrorCode.SnapshotInvalid, "The requested SnapshotId does not match the manifest.");
            }

            var metadataDirectory = Path.GetDirectoryName(manifestPath)!;
            var snapshotRoot = Directory.GetParent(metadataDirectory)?.FullName
                ?? throw new AppFailureException(ErrorCode.SnapshotInvalid, "The snapshot manifest has no snapshot root.");
            return await new RawSnapshotVerifier().VerifyAsync(new RawSnapshot(manifest, snapshotRoot), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Transport-level failures (protocol, cancellation, snapshot-not-found,
    /// internal runtime errors) carry a typed transport code; domain failures
    /// carry an <see cref="ErrorCode"/> name. Clients map each kind to the
    /// matching enum and never surface raw strings.
    /// </summary>
    private static BrokerResponse Failed(string? requestId, string code, string message) =>
        new("failed", requestId, null, null, new BrokerError(
            BrokerErrorKind.Transport,
            code,
            message));

    private static BrokerResponse Failed(string? requestId, AppError error, string? message) =>
        new("failed", requestId, null, null, new BrokerError(
            BrokerErrorKind.Domain,
            error.Code.ToString(),
            string.IsNullOrWhiteSpace(message) ? error.NonSensitiveTechnicalContext : message,
            error.IsRetryable,
            error.SuggestedAction,
            error.NonSensitiveTechnicalContext));

    private static int Fail(
        BrokerRequest? request,
        ErrorCode code,
        string? message,
        bool snapshotVerified,
        bool snapshotStaged,
        Action<BrokerStageEvent>? reportStage,
        TextWriter output)
    {
        reportStage?.Invoke(new BrokerStageEvent(code switch
        {
            ErrorCode.SnapshotInvalid or ErrorCode.SnapshotInconsistent => "operation-failed-snapshot",
            ErrorCode.WeixinNotRunning or ErrorCode.UnsupportedWeixinVersion
                or ErrorCode.ProcessIdentityMismatch or ErrorCode.KeyCandidateNotFound => "operation-failed-acquisition",
            ErrorCode.WorkerBundleUntrusted or ErrorCode.WorkerFailed => "operation-failed-worker",
            ErrorCode.WorkspaceInvalid => "operation-failed-workspace",
            _ => "operation-failed-materialization",
        }));
        var exitCode = code switch
        {
            ErrorCode.WeixinNotRunning or ErrorCode.UnsupportedWeixinVersion
                or ErrorCode.ProcessIdentityMismatch or ErrorCode.KeyCandidateNotFound => 3,
            ErrorCode.SnapshotInvalid or ErrorCode.SnapshotInconsistent when !snapshotStaged => 4,
            _ => 1,
        };
        BrokerProtocol.Write(output, Failed(request?.RequestId, ErrorCatalog.Get(code), message));
        return exitCode;
    }
}
