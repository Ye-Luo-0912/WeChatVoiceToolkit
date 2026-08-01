using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Snapshots;
using WeChatVoice.Windows;

namespace WeChatVoice.Workflows.Workflows;

/// <summary>
/// Creates a stable file-level snapshot and derives the account identity
/// candidate from the verified source layout. The manifest path is the
/// reserved <c>.wechatvoice/snapshot-manifest.json</c> inside the output.
/// </summary>
public sealed class SnapshotWorkflow(ISnapshotCreator creator) : ISnapshotWorkflow
{
    public SnapshotWorkflow()
        : this(new SnapshotCreator(new WeChatSnapshotSourceActivityProbe()))
    {
    }

    public async Task<SnapshotWorkflowResult> RunAsync(
        SnapshotWorkflowRequest request,
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
            context.Report(OperationPhase.Snapshot, OperationStageIds.Preparing);
            var requestModel = new SnapshotRequest(
                request.SourceDirectory,
                request.OutputDirectory,
                AllowLiveSource: request.AllowLiveSource,
                MaxAttempts: request.MaxAttempts);
            var manifest = await creator.CreateAsync(requestModel, cancellationToken).ConfigureAwait(false);
            var identity = SnapshotSourceIdentity.TryDerive(manifest.SourceDirectory, manifest.Files);
            var manifestPath = Path.Combine(Path.GetFullPath(request.OutputDirectory), ".wechatvoice", "snapshot-manifest.json");
            context.StateMachine.TryComplete();
            context.Report(OperationPhase.Snapshot, OperationStageIds.Completing);
            return new SnapshotWorkflowResult(manifest, identity, manifestPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            context.StateMachine.TryCancel();
            context.Report(OperationPhase.Snapshot, OperationStageIds.Starting, "快照创建已取消");
            throw;
        }
        catch
        {
            context.StateMachine.TryFail();
            throw;
        }
    }
}
