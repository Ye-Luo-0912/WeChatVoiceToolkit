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
        MaterializationProvenance? Provenance = null)
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
    }

    public string WorkspaceId { get; }

    public string SourceRoot { get; }

    public WeChatDataSet DataSet { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public IReadOnlyList<DataSetIssue> Issues { get; }

    public IReadOnlyList<AdapterCandidate> AdapterCandidates { get; }

    public MaterializationProvenance? Provenance { get; }

    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T>? values)
        => new ReadOnlyCollection<T>((values ?? Array.Empty<T>()).ToArray());
}

public sealed record MaterializationProvenance(
    string SourceSnapshotId,
    string MaterializationId,
    string BackendId,
    string BackendVersion,
    string BackendBundleSha256,
    string MaterializationManifestSha256);
