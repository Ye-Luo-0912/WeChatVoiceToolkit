using WeChatVoice.Application;
using WeChatVoice.Core.Models;

namespace WeChatVoice.Workflows.Workflows;

/// <summary>
/// Audits matching voice metadata without writing any payload file. Payload
/// states (Missing/Empty/InvalidHeader/Ambiguous) are counted in the report
/// and remain visible to the host; only Linked voices are eligible for export.
/// </summary>
public sealed class VoiceScanWorkflow(
    Workspaces.VoiceCatalogOpener opener,
    Workspaces.ContactResolver resolver) : IVoiceScanWorkflow
{
    public async Task<VoiceScanWorkflowResult> RunAsync(
        VoiceScanWorkflowRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!context.StateMachine.TryStart())
        {
            throw new InvalidOperationException("The workflow state machine is not idle.");
        }

        try
        {
            context.Report(OperationPhase.VoiceScan, OperationStageIds.LoadingWorkspace);
            await using var session = await opener.OpenAsync(request.WorkspacePath, cancellationToken).ConfigureAwait(false);
            context.Report(OperationPhase.VoiceScan, OperationStageIds.ResolvingContact);
            var contact = await resolver.ResolveExactAsync(session.Catalog, request.ContactUsername, cancellationToken).ConfigureAwait(false);
            var query = VoiceQueryBuilder.Build(request.ConversationId, contact, request.Direction, request.From, request.To);
            context.Report(OperationPhase.VoiceScan, OperationStageIds.QueryingVoices);
            var report = await new VoiceScanService(session.Catalog).ScanAsync(query, cancellationToken).ConfigureAwait(false);
            context.StateMachine.TryComplete();
            context.Report(OperationPhase.VoiceScan, OperationStageIds.Completing);
            return new VoiceScanWorkflowResult(report, session.Workspace);
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
}
