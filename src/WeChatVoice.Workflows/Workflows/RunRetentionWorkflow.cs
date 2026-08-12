using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Export;

namespace WeChatVoice.Workflows.Workflows;

/// <summary>
/// Run / metadata retention workflow for an export root's <c>runs/</c>
/// directory. Preview is read-only and never deletes; compact removes only the
/// journal and transaction metadata of older unreferenced runs through
/// <see cref="RunRetentionService"/>, which always retains committed manifests,
/// CSV, artifact index, and the metadata-commit descriptor. A run bound to a
/// dataset selection profile is never compacted, and the <c>latest</c> aliases
/// are never the sole authority for what may be removed.
/// </summary>
public sealed class RunRetentionWorkflow : IRunRetentionWorkflow
{
    private readonly RunRetentionService _service;

    public RunRetentionWorkflow(RunRetentionService? service = null)
    {
        _service = service ?? new RunRetentionService();
    }

    public async Task<RunRetentionPreview> PreviewAsync(
        RunRetentionOptions options,
        WorkflowContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.TryStart())
        {
            throw new InvalidOperationException("The workflow state machine is not idle.");
        }

        try
        {
            context.Report(OperationPhase.RunRetention, OperationStageIds.InspectingRuns, "正在检查导出 run 的保留状态");
            var preview = await _service.PreviewAsync(options, cancellationToken).ConfigureAwait(false);
            context.StateMachine.TryComplete();
            context.Report(OperationPhase.RunRetention, OperationStageIds.Completing);
            return preview;
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

    public async Task<RunRetentionResult> CompactAsync(
        RunRetentionOptions options,
        WorkflowContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.TryStart())
        {
            throw new InvalidOperationException("The workflow state machine is not idle.");
        }

        try
        {
            context.Report(OperationPhase.RunRetention, OperationStageIds.CompactingRuns, "正在压缩过期且未引用的 run 元数据");
            var result = await _service.CompactAsync(options, cancellationToken).ConfigureAwait(false);
            context.StateMachine.TryComplete();
            context.Report(OperationPhase.RunRetention, OperationStageIds.Completing, "run 保留压缩完成");
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
}
