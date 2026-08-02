using System.Collections.ObjectModel;

namespace WeChatVoice.Core.Models;

/// <summary>
/// A deliberately local-only executable workspace. Unlike <see cref="DataSetProbe"/>,
/// this document contains absolute database paths and must never be uploaded or
/// committed to source control.
/// </summary>
public sealed record LocalWorkspace
{
    public LocalWorkspace(
        string WorkspaceId,
        string SourceRoot,
        WeChatDataSet DataSet,
        DateTimeOffset CreatedAtUtc,
        IReadOnlyList<DataSetIssue>? Issues = null,
        IReadOnlyList<AdapterCandidate>? AdapterCandidates = null,
        MaterializationProvenance? Provenance = null,
        SnapshotSourceIdentity? SourceIdentity = null,
        AccountIdentity? AccountIdentity = null)
    {
        if (string.IsNullOrWhiteSpace(WorkspaceId))
        {
            throw new ArgumentException("A workspace identifier is required.", nameof(WorkspaceId));
        }

        if (string.IsNullOrWhiteSpace(SourceRoot) || !Path.IsPathFullyQualified(SourceRoot))
        {
            throw new ArgumentException("A workspace source root must be an absolute path.", nameof(SourceRoot));
        }

        ArgumentNullException.ThrowIfNull(DataSet);
        if (DataSet.Databases.Any(static artifact => string.IsNullOrWhiteSpace(artifact.LocalPath)))
        {
            throw new ArgumentException("An executable workspace requires a local path for every database artifact.", nameof(DataSet));
        }

        this.WorkspaceId = WorkspaceId;
        this.SourceRoot = Path.GetFullPath(SourceRoot);
        this.DataSet = DataSet;
        this.CreatedAtUtc = CreatedAtUtc.ToUniversalTime();
        this.Issues = Freeze(Issues);
        this.AdapterCandidates = Freeze(AdapterCandidates);
        this.Provenance = Provenance;
        this.SourceIdentity = SourceIdentity;
        this.AccountIdentity = AccountIdentity ?? AccountIdentity.CandidateOnly;
    }

    public string WorkspaceId { get; }

    public string SourceRoot { get; }

    public WeChatDataSet DataSet { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public IReadOnlyList<DataSetIssue> Issues { get; }

    public IReadOnlyList<AdapterCandidate> AdapterCandidates { get; }

    public MaterializationProvenance? Provenance { get; }

    public SnapshotSourceIdentity? SourceIdentity { get; }

    /// <summary>
    /// Identity evidence and the separate user confirmation decision carried by
    /// this local workspace. A path-derived account can remain Candidate even
    /// after the user confirms that it is the intended account.
    /// </summary>
    public AccountIdentity AccountIdentity { get; }

    public LocalWorkspace WithAccountIdentity(AccountIdentity accountIdentity)
    {
        ArgumentNullException.ThrowIfNull(accountIdentity);
        return new LocalWorkspace(
            WorkspaceId,
            SourceRoot,
            DataSet,
            CreatedAtUtc,
            Issues,
            AdapterCandidates,
            Provenance,
            SourceIdentity,
            accountIdentity);
    }

    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T>? values)
        => new ReadOnlyCollection<T>((values ?? Array.Empty<T>()).ToArray());
}

public sealed record MaterializationProvenance(
    string SourceSnapshotId,
    string MaterializationId,
    string BackendId,
    string BackendVersion,
    string BackendBundleSha256,
    string MaterializationManifestSha256,
    string? KeyExtractionProfileId = null,
    string? ProcessVersion = null,
    string? ProcessImageSha256 = null,
    string? WcdbModuleSha256 = null,
    string? AccountSidFingerprint = null);
