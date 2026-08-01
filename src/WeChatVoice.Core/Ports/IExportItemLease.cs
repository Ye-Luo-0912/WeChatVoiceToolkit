using WeChatVoice.Core.Models;

namespace WeChatVoice.Core.Ports;

/// <summary>
/// Store-owned reservation and transactional persistence boundary for one
/// voice item. Paths exposed here are portable manifest paths only.
/// </summary>
public interface IExportItemLease : IAsyncDisposable
{
    VoiceRecord Record { get; }

    ExportArtifactState OriginalState { get; }

    ExportArtifactState DecodedState { get; }

    ExportArtifact? ExistingOriginalArtifact { get; }

    ExportArtifact? ExistingDecodedArtifact { get; }

    string OriginalManifestPath { get; }

    string DecodedManifestPath { get; }

    ValueTask<Stream> OpenOriginalWriteAsync(CancellationToken cancellationToken);

    ValueTask<Stream> OpenOriginalReadAsync(CancellationToken cancellationToken);

    Task<ExportArtifact> CommitOriginalAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Commits using the artifact computed while the caller copied the source
    /// stream. Stores may still validate the temporary file length; they do not
    /// need to reread it solely to calculate the same hash.
    /// </summary>
    Task<ExportArtifact> CommitOriginalAsync(ExportArtifact computedArtifact, CancellationToken cancellationToken)
        => CommitOriginalAsync(cancellationToken);

    ValueTask<Stream> OpenDecodedWriteAsync(CancellationToken cancellationToken);

    Task<ExportArtifact> CommitDecodedAsync(CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);
}
