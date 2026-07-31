namespace WeChatVoice.Core.Models;

/// <summary>
/// A LocalWorkspace whose paths and database-group content were revalidated
/// immediately before use. Adapters must not accept an unverified JSON model.
/// </summary>
public sealed record VerifiedLocalWorkspace
{
    public VerifiedLocalWorkspace(LocalWorkspace Workspace, DateTimeOffset VerifiedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(Workspace);
        this.Workspace = Workspace;
        this.VerifiedAtUtc = VerifiedAtUtc.ToUniversalTime();
    }

    public LocalWorkspace Workspace { get; }

    public WeChatDataSet DataSet => Workspace.DataSet;

    public DateTimeOffset VerifiedAtUtc { get; }
}
