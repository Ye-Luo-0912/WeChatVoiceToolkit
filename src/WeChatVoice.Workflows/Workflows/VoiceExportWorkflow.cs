using WeChatVoice.Application;
using WeChatVoice.Core.Models;
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
    Workspaces.ContactResolver resolver) : IVoiceExportWorkflow
{
    public async Task<VoiceExportWorkflowResult> RunAsync(
        VoiceExportWorkflowRequest request,
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
            context.Report(OperationPhase.VoiceExport, OperationStageIds.LoadingWorkspace);
            await using var session = await opener.OpenAsync(request.WorkspacePath, cancellationToken).ConfigureAwait(false);
            context.Report(OperationPhase.VoiceExport, OperationStageIds.ResolvingContact);
            var contact = await resolver.ResolveExactAsync(session.Catalog, request.ContactUsername, cancellationToken).ConfigureAwait(false);
            var query = VoiceQueryBuilder.Build(request.ConversationId, contact, request.Direction, request.From, request.To);
            var service = new VoiceExportService(session.Catalog, new FileSystemVoiceExportStore(request.OutputDirectory));
            context.Report(OperationPhase.VoiceExport, OperationStageIds.Exporting);
            var manifest = await service.ExportAsync(query, new VoiceExportOptions { DecodeToWav = false }, cancellationToken).ConfigureAwait(false);
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

    public async Task<VoiceExportManifest> RecoverRunAsync(
        string journalPath,
        CancellationToken cancellationToken)
    {
        var fullJournalPath = Path.GetFullPath(journalPath);
        var exportRoot = Path.GetDirectoryName(Path.GetDirectoryName(fullJournalPath)!)
            ?? throw new InvalidDataException("The Journal path must be nested under an export root runs directory.");
        return await new FileSystemVoiceExportStore(exportRoot).RecoverRunAsync(fullJournalPath, cancellationToken).ConfigureAwait(false);
    }
}
