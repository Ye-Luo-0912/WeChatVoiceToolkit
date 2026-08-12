using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.SeedVc;

namespace WeChatVoice.Workflows.Workflows;

public sealed class SeedVcWorkflow : ISeedVcWorkflow
{
    private readonly SeedVcDoctorService _doctor = new();
    private readonly SeedVcPrepareService _prepare = new();
    private readonly SeedVcTrainService _train = new();
    private readonly SeedVcInferService _infer = new();

    public async Task<SeedVcDoctorReport> DoctorAsync(SeedVcDoctorRequest request, WorkflowContext context, CancellationToken cancellationToken)
    {
        EnsureStart(context);
        try { context.Report(OperationPhase.SeedVc, OperationStageIds.CheckingSeedVc, "检查 Seed-VC 环境"); var result = await _doctor.CheckAsync(request, cancellationToken).ConfigureAwait(false); context.StateMachine.TryComplete(); return result; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { context.StateMachine.TryCancel(); throw; }
        catch { context.StateMachine.TryFail(); throw; }
    }

    public async Task<SeedVcPrepareResult> PrepareAsync(SeedVcPrepareRequest request, WorkflowContext context, CancellationToken cancellationToken)
    {
        EnsureStart(context);
        try { context.Report(OperationPhase.SeedVc, OperationStageIds.PreparingSeedVc, "准备 Seed-VC 训练音频"); var result = await _prepare.PrepareAsync(request, cancellationToken).ConfigureAwait(false); context.StateMachine.TryComplete(); return result; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { context.StateMachine.TryCancel(); throw; }
        catch { context.StateMachine.TryFail(); throw; }
    }

    public async Task<SeedVcTrainResult> TrainAsync(SeedVcTrainRequest request, WorkflowContext context, CancellationToken cancellationToken)
    {
        EnsureStart(context);
        try { context.Report(OperationPhase.SeedVc, OperationStageIds.TrainingSeedVc, "启动 Seed-VC 微调"); var result = await _train.TrainAsync(request, context.Progress, cancellationToken).ConfigureAwait(false); context.StateMachine.TryComplete(); return result; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { context.StateMachine.TryCancel(); throw; }
        catch { context.StateMachine.TryFail(); throw; }
    }

    public async Task<SeedVcInferResult> InferAsync(SeedVcInferRequest request, WorkflowContext context, CancellationToken cancellationToken)
    {
        EnsureStart(context);
        try { context.Report(OperationPhase.SeedVc, OperationStageIds.InferringSeedVc, "运行 Seed-VC 音色转换"); var result = await _infer.InferAsync(request, context.Progress, cancellationToken).ConfigureAwait(false); context.StateMachine.TryComplete(); return result; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { context.StateMachine.TryCancel(); throw; }
        catch { context.StateMachine.TryFail(); throw; }
    }

    private static void EnsureStart(WorkflowContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.TryStart()) throw new InvalidOperationException("The workflow state machine is not idle.");
    }
}
