using WeChatVoice.Application;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;

namespace WeChatVoice.Workflows.Workflows;

/// <summary>
/// Audits matching voice metadata without writing any payload file. Payload
/// states (Missing/Empty/InvalidHeader/Ambiguous) are counted in the report
/// and remain visible to the host; only Linked voices are eligible for export.
/// </summary>
public sealed class VoiceScanWorkflow(
    Workspaces.VoiceCatalogOpener opener,
    Workspaces.ContactResolver resolver,
    IVoiceDurationResolver? durationResolver = null,
    Func<VerifiedLocalWorkspace, IVoiceDurationCache>? durationCacheFactory = null,
    Func<VerifiedLocalWorkspace, IVoicePayloadHashCache>? deepScanCacheFactory = null,
    ITemporaryFileCleanupQueue? cleanupQueue = null,
    ScanCacheService? scanCache = null) : IVoiceScanWorkflow
{
    public async Task<VoiceScanWorkflowResult> RunAsync(
        VoiceScanWorkflowRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!context.TryStart())
        {
            throw new InvalidOperationException("The workflow state machine is not idle.");
        }

        try
        {
            context.Report(OperationPhase.VoiceScan, OperationStageIds.LoadingWorkspace);
            await using var session = await opener.OpenAsync(request.WorkspacePath, cancellationToken).ConfigureAwait(false);
            await using var durationCache = durationCacheFactory?.Invoke(session.Workspace);
            await using var deepScanCache = deepScanCacheFactory?.Invoke(session.Workspace);
            context.Report(OperationPhase.VoiceScan, OperationStageIds.ResolvingContact);
            var contact = await resolver.ResolveExactAsync(session.Catalog, request.ContactUsername, cancellationToken).ConfigureAwait(false);
            EnsureExpectedContact(request.ExpectedContactId, contact);
            var query = VoiceQueryBuilder.Build(request.ConversationId, contact, request.Direction ?? VoiceDirection.Incoming, request.From, request.To,
                request.MaximumResults, request.DeepScan, request.ResolveDurations,
                request.MinimumDurationMs, request.MaximumDurationMs,
                request.MinimumPayloadBytes, request.MaximumPayloadBytes);
            var effectiveDurationResolver = durationResolver is not null && durationCache is not null
                ? new CachedVoiceDurationResolver(durationResolver, durationCache, cleanupQueue)
                : durationResolver;
            var durationProgress = request.ResolveDurations
                ? new InlineProgress<DurationResolutionProgress>(value =>
                {
                    var percent = value.Attempted > 0
                        ? Math.Clamp(value.Resolved * 100.0 / value.Attempted, 0, 100)
                        : (double?)null;
                    context.Report(
                        OperationPhase.VoiceScan,
                        OperationStageIds.ResolvingDurations,
                        $"{value.Resolved}/{value.Attempted}",
                        percent);
                })
                : null;
            var durationResolverVersion = SelectionIdentity.DurationResolverVersion(effectiveDurationResolver);
            var queryFingerprint = scanCache is null
                ? null
                : PreparedVoiceSelection.ComputeQueryFingerprint(
                    session.Workspace.Workspace.WorkspaceId,
                    session.Catalog.Context,
                    contact,
                    query,
                    PreparedVoiceSelection.CurrentSelectionEngineVersion,
                    durationResolverVersion);

            PreparedVoiceSelection selection;
            if (scanCache is not null && queryFingerprint is not null)
            {
                var cached = await scanCache.TryReadAsync(session.Workspace.Workspace.WorkspaceId, queryFingerprint, cancellationToken).ConfigureAwait(false);
                if (cached is not null)
                {
                    context.Report(OperationPhase.VoiceScan, OperationStageIds.UsingCachedSelection);
                    selection = PreparedVoiceSelection.Create(
                        request.WorkspacePath,
                        session.Workspace,
                        session.Catalog.Context,
                        contact,
                        query,
                        cached.Report,
                        durationResolverVersion,
                        cached.Records,
                        cached.Spool);
                    context.StateMachine.TryComplete();
                    context.Report(OperationPhase.VoiceScan, OperationStageIds.Completing);
                    return new VoiceScanWorkflowResult(cached.Report, session.Workspace, selection);
                }
            }

            context.Report(OperationPhase.VoiceScan, OperationStageIds.QueryingVoices);
            var scan = await new VoiceScanService(session.Catalog, effectiveDurationResolver, deepScanCache, cleanupQueue)
                .ScanWithRecordsAsync(query, durationProgress, cancellationToken)
                .ConfigureAwait(false);
            if (scanCache is not null && queryFingerprint is not null)
            {
                await scanCache.WriteAsync(
                    session.Workspace.Workspace.WorkspaceId,
                    queryFingerprint,
                    scan.Report,
                    scan.Records,
                    scan.Spool,
                    cancellationToken).ConfigureAwait(false);
            }

            selection = PreparedVoiceSelection.Create(
                request.WorkspacePath,
                session.Workspace,
                session.Catalog.Context,
                contact,
                query,
                scan.Report,
                durationResolverVersion,
                scan.Records,
                scan.Spool);
            context.StateMachine.TryComplete();
            context.Report(OperationPhase.VoiceScan, OperationStageIds.Completing);
            return new VoiceScanWorkflowResult(scan.Report, session.Workspace, selection);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            context.StateMachine.TryCancel();
            throw;
        }
        catch
        {
            context.StateMachine.TryFail();
            throw;
        }
    }

    private static void EnsureExpectedContact(string? expectedContactId, ContactRecord contact)
    {
        if (!string.IsNullOrWhiteSpace(expectedContactId)
            && !string.Equals(expectedContactId, contact.ContactId, StringComparison.Ordinal))
        {
            throw new Core.Errors.AppFailureException(
                Core.Errors.ErrorCode.SelectionPlanMismatch,
                "The resolved contact no longer matches the selected stable ContactId.");
        }
    }

    /// <summary>
    /// Invokes the callback inline on the calling thread so every progress
    /// update reaches the workflow context before the scan task completes.
    /// Unlike <see cref="Progress{T}"/>, this never defers delivery through a
    /// SynchronizationContext and therefore cannot drop the final update.
    /// </summary>
    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
