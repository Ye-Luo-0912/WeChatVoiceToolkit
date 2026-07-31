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

    ValueTask<Stream> OpenDecodedWriteAsync(CancellationToken cancellationToken);

    Task<ExportArtifact> CommitDecodedAsync(CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);
}
