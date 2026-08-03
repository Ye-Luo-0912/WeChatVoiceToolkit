using WeChatVoice.Application;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Export;

namespace WeChatVoice.Workflows.Workflows;

/// <summary>
/// Exports raw SILK voices for exactly one 1:1 contact. The source BLOB is
/// read once: copied, hashed, compared with existing artifacts, and committed
/// under a portable hash-derived path. Decoded WAV output is deliberately not
/// part of this workflow; the first usable chain is raw SILK only.
/// </summary>
public sealed class VoiceExportWorkflow(
    Workspaces.VoiceCatalogOpener opener,
    Workspaces.ContactResolver resolver,
    Func<VerifiedLocalWorkspace, IVoiceDurationCache>? durationCacheFactory = null,
    IVoiceDurationResolver? durationResolver = null,
    ExportVerificationService? verificationService = null) : IVoiceExportWorkflow
{
    private readonly ExportVerificationService _verificationService = verificationService ?? new();

    public async Task<VoiceExportWorkflowResult> RunAsync(
        PreparedVoiceSelection plan,
        ExportDestination destination,
        WorkflowContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(context);
        if (!context.TryStart())
        {
            throw new InvalidOperationException("The workflow state machine is not idle.");
        }

        try
        {
            context.Report(OperationPhase.VoiceExport, OperationStageIds.LoadingWorkspace);
            await using var session = await opener.OpenAsync(plan.WorkspaceDocumentPath, cancellationToken).ConfigureAwait(false);
            await using var durationCache = durationCacheFactory?.Invoke(session.Workspace);
            var effectiveDurationResolver = durationResolver is not null && durationCache is not null
                ? new CachedVoiceDurationResolver(durationResolver, durationCache)
                : durationResolver;
            ValidatePlanIdentity(plan, session, effectiveDurationResolver);
            context.Report(OperationPhase.VoiceExport, OperationStageIds.ResolvingContact);
            var contact = await resolver.ResolveExactAsync(session.Catalog, plan.ContactUsername, cancellationToken).ConfigureAwait(false);
            EnsurePlanContact(plan, contact);
            var query = VoiceQueryBuilder.Build(
                conversationId: null,
                contact,
                plan.Direction,
                plan.FromUtc,
                plan.ToUtc,
                plan.MaximumResults,
                plan.DeepScan,
                plan.ResolveDurations,
                plan.MinimumDurationMs,
                plan.MaximumDurationMs,
                plan.MinimumPayloadBytes,
                plan.MaximumPayloadBytes);
            ValidatePlanQuery(plan, session.Catalog.Context, contact, query, effectiveDurationResolver);
            var service = new VoiceExportService(
                session.Catalog,
                new FileSystemVoiceExportStore(destination.OutputDirectory),
                durationCache: durationCache,
                durationResolver: effectiveDurationResolver);
            context.Report(OperationPhase.VoiceExport, OperationStageIds.Exporting);
            var options = new VoiceExportOptions
            {
                DecodeToWav = false,
                ExpectedResultSetFingerprint = plan.ResultSetFingerprint,
                ExpectedResultCount = plan.ResultCount,
                ExpectedTotalPayloadBytes = plan.TotalPayloadBytes,
            };
            var manifest = plan.HasPreparedRecords
                ? await service.ExportPreparedAsync(query, options, plan.Records, cancellationToken).ConfigureAwait(false)
                : await service.ExportAsync(query, options, cancellationToken).ConfigureAwait(false);
            context.StateMachine.TryComplete();
            context.Report(OperationPhase.VoiceExport, OperationStageIds.Completing);
            return new VoiceExportWorkflowResult(manifest, session.Workspace);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            context.StateMachine.TryCancel();
            context.Report(OperationPhase.VoiceExport, OperationStageIds.Starting, "导出已取消");
            throw;
        }
        catch
        {
            context.StateMachine.TryFail();
            throw;
        }
    }

    /// <summary>
    /// Compatibility adapter for older CLI/test callers. New hosts must run
    /// Scan first and pass its prepared selection to the formal overload.
    /// </summary>
    public async Task<VoiceExportWorkflowResult> RunAsync(
        VoiceExportWorkflowRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scan = new VoiceScanWorkflow(
            opener,
            resolver,
            durationResolver,
            durationCacheFactory,
            deepScanCacheFactory: null);
        VoiceScanWorkflowResult scanResult;
        try
        {
            scanResult = await scan.RunAsync(
                new VoiceScanWorkflowRequest(
                    WorkspacePath: request.WorkspacePath,
                    ContactUsername: request.ContactUsername,
                    ConversationId: request.ConversationId,
                    Direction: request.Direction,
                    From: request.From,
                    To: request.To,
                    MaximumResults: request.MaximumResults,
                    DeepScan: false,
                    ResolveDurations: request.ResolveDurations,
                    ExpectedContactId: request.ExpectedContactId,
                    MinimumDurationMs: request.MinimumDurationMs,
                    MaximumDurationMs: request.MaximumDurationMs,
                    MinimumPayloadBytes: request.MinimumPayloadBytes,
                    MaximumPayloadBytes: request.MaximumPayloadBytes),
                new WorkflowContext(new LegacyAccountConfirmation()),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            context.TryStart();
            context.StateMachine.TryCancel();
            throw;
        }
        catch
        {
            context.TryStart();
            context.StateMachine.TryFail();
            throw;
        }
        var prepared = scanResult.Selection
            ?? throw new InvalidDataException("The compatibility scan did not produce a prepared selection.");
        return await RunAsync(prepared, new ExportDestination(request.OutputDirectory), context, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VoiceExportManifest> RecoverRunAsync(
        string journalPath,
        CancellationToken cancellationToken)
    {
        var fullJournalPath = Path.GetFullPath(journalPath);
        var exportRoot = Path.GetDirectoryName(Path.GetDirectoryName(fullJournalPath)!)
            ?? throw new InvalidDataException("The Journal path must be nested under an export root runs directory.");
        return await new FileSystemVoiceExportStore(exportRoot).RecoverRunAsync(fullJournalPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExportVerificationResult> VerifyAsync(
        ExportVerificationRequest request,
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
            context.Report(OperationPhase.VoiceExport, OperationStageIds.LoadingWorkspace, "验证导出 Manifest 与 SILK");
            var result = await _verificationService.VerifyAsync(request.ExportDirectory, request.RunId, cancellationToken).ConfigureAwait(false);
            context.StateMachine.TryComplete();
            context.Report(OperationPhase.VoiceExport, OperationStageIds.Completing);
            return result;
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

    public async Task<ExportRepairResult> RepairAsync(
        ExportRepairRequest request,
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
            context.Report(OperationPhase.VoiceExport, OperationStageIds.LoadingWorkspace, "验证并修复导出派生文件");
            var result = await _verificationService.RepairAsync(request.ExportDirectory, request.RunId, cancellationToken).ConfigureAwait(false);
            context.StateMachine.TryComplete();
            context.Report(OperationPhase.VoiceExport, OperationStageIds.Completing);
            return result;
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

    private static void ValidatePlanIdentity(
        PreparedVoiceSelection plan,
        Workspaces.CatalogSession session,
        IVoiceDurationResolver? durationResolver)
    {
        var catalogContext = session.Catalog.Context;
        var workspace = session.Workspace.Workspace;
        if (!string.Equals(plan.WorkspaceId, workspace.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(plan.DatasetId, catalogContext.DatasetId, StringComparison.Ordinal)
            || !string.Equals(plan.AccountId, catalogContext.AccountId, StringComparison.Ordinal)
            || !string.Equals(plan.SnapshotId, catalogContext.SnapshotId, StringComparison.Ordinal)
            || !string.Equals(plan.AdapterId, catalogContext.AdapterId, StringComparison.Ordinal)
            || !string.Equals(plan.AdapterVersion, catalogContext.AdapterVersion, StringComparison.Ordinal)
            || !string.Equals(plan.SelectionEngineVersion, PreparedVoiceSelection.CurrentSelectionEngineVersion, StringComparison.Ordinal)
            || !string.Equals(plan.DurationResolverVersion, SelectionIdentity.DurationResolverVersion(durationResolver), StringComparison.Ordinal))
        {
            throw new Core.Errors.AppFailureException(
                Core.Errors.ErrorCode.SelectionPlanMismatch,
                "The prepared voice selection no longer matches the verified Workspace or selection engine.");
        }
    }

    private static void EnsurePlanContact(PreparedVoiceSelection plan, ContactRecord contact)
    {
        if (!string.Equals(plan.ContactId, contact.ContactId, StringComparison.Ordinal)
            || !string.Equals(plan.ContactUsername, contact.Username, StringComparison.Ordinal))
        {
            throw new Core.Errors.AppFailureException(
                Core.Errors.ErrorCode.SelectionPlanMismatch,
                "The resolved contact no longer matches the prepared selection.");
        }
    }

    private static void ValidatePlanQuery(
        PreparedVoiceSelection plan,
        VoiceCatalogContext context,
        ContactRecord contact,
        VoiceQuery query,
        IVoiceDurationResolver? durationResolver)
    {
        var current = PreparedVoiceSelection.ComputeQueryFingerprint(
            plan.WorkspaceId,
            context,
            contact,
            query,
            PreparedVoiceSelection.CurrentSelectionEngineVersion,
            SelectionIdentity.DurationResolverVersion(durationResolver));
        if (!string.Equals(current, plan.QueryFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new Core.Errors.AppFailureException(
                Core.Errors.ErrorCode.SelectionPlanMismatch,
                "The prepared voice query fingerprint no longer matches the verified catalog.");
        }
    }

    private sealed class LegacyAccountConfirmation : Core.Ports.IAccountConfirmation
    {
        public Task<AccountConfirmation> ConfirmAsync(AccountIdentityReport report, CancellationToken cancellationToken)
            => Task.FromResult(new AccountConfirmation(true, report.AccountCandidate));
    }
}
