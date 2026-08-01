using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;

namespace WeChatVoice.Workflows.Workspaces;

/// <summary>
/// The canonical "verify workspace -> resolve adapter -> open catalog" path
/// shared by every voice-facing workflow. Hosts never compose these steps
/// themselves; the opener keeps the verified-type boundary intact.
/// </summary>
public sealed class VoiceCatalogOpener
{
    private readonly WorkspaceLoader _loader;
    private readonly IWeChatDataSetAdapterResolver _resolver;

    public VoiceCatalogOpener(WorkspaceLoader loader, IWeChatDataSetAdapterResolver resolver)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public async Task<CatalogSession> OpenAsync(string workspacePath, CancellationToken cancellationToken)
    {
        var verified = await _loader.LoadVerifiedAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var adapter = _resolver.Resolve(verified.DataSet);
        var catalog = await adapter.OpenAsync(verified, cancellationToken).ConfigureAwait(false);
        return new CatalogSession(verified, catalog);
    }
}

/// <summary>
/// Bounds a verified workspace and its open catalog. Disposing the session
/// disposes the catalog; every host must <c>await using</c> it.
/// </summary>
public sealed record CatalogSession(VerifiedLocalWorkspace Workspace, IVoiceCatalog Catalog) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Catalog.DisposeAsync();
}
