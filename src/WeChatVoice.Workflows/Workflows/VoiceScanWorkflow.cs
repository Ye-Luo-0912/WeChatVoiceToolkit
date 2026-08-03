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
    ITemporaryFileCleanupQueue? cleanupQueue = null) : IVoiceScanWorkflow
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
            context.Report(OperationPhase.VoiceScan, OperationStageIds.QueryingVoices);
            var effectiveDurationResolver = durationResolver is not null && durationCache is not null
                ? new CachedVoiceDurationResolver(durationResolver, durationCache, cleanupQueue)
                : durationResolver;
            var scan = await new VoiceScanService(session.Catalog, effectiveDurationResolver, deepScanCache, cleanupQueue)
                .ScanWithRecordsAsync(query, cancellationToken)
                .ConfigureAwait(false);
            var selection = PreparedVoiceSelection.Create(
                request.WorkspacePath,
                session.Workspace,
                session.Catalog.Context,
                contact,
                query,
                scan.Report,
                SelectionIdentity.DurationResolverVersion(effectiveDurationResolver),
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
}
