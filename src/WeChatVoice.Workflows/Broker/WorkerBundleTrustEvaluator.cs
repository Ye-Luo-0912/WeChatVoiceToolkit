using InfrastructureWorkerBundleTrustEvaluator = WeChatVoice.Infrastructure.Trust.WorkerBundleTrustEvaluator;

namespace WeChatVoice.Workflows.Broker;

/// <summary>Workflow-facing adapter for the shared Worker closure verifier.</summary>
public static class WorkerBundleTrustEvaluator
{
    public static async Task<WorkerBundleTrustResult> VerifyAsync(string directory, CancellationToken cancellationToken)
    {
        var result = await InfrastructureWorkerBundleTrustEvaluator.VerifyAsync(directory, cancellationToken).ConfigureAwait(false);
        return result.Verified
            ? WorkerBundleTrustResult.Ok()
            : WorkerBundleTrustResult.Deny(result.NonSensitiveReason ?? "worker-bundle-untrusted");
    }
}
