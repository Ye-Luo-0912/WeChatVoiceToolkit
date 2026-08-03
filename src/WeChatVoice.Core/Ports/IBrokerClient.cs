using WeChatVoice.Core.Models;

namespace WeChatVoice.Core.Ports;

/// <summary>
/// Normal-privilege port for the one-shot elevated Key Broker. Implementations
/// verify the Broker binary trust policy, launch it elevated, bind the named
/// pipe to the exact process started, and translate the terminal response into
/// typed exceptions (<see cref="WeChatVoice.Core.Errors.AppFailureException"/>
/// for domain failures, <see cref="WeChatVoice.Core.Errors.BrokerTransportException"/>
/// for transport failures, <see cref="UnauthorizedAccessException"/> when
/// elevation was declined). Hosts depend on this port, never on a Broker
/// implementation.
/// </summary>
public interface IBrokerClient
{
    Task<BrokerResponse> AcquireAndMaterializeAsync(
        VerifiedRawSnapshot snapshot,
        string snapshotManifestPath,
        string outputRoot,
        string workspaceOutput,
        CancellationToken cancellationToken,
        IProgress<OperationProgress>? progress = null);

    /// <summary>
    /// Starts the elevated Broker's no-data self-test. Implementations must
    /// not read a Snapshot, database, or process memory for this operation.
    /// </summary>
    Task<BrokerSelfTestResponse> SelfTestAsync(CancellationToken cancellationToken)
        => Task.FromException<BrokerSelfTestResponse>(new NotSupportedException("Broker self-test is not supported by this client."));
}
