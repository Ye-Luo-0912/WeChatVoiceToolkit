using System.Collections.ObjectModel;

namespace WeChatVoice.Core.Models;

public sealed record RawSnapshot
{
    public RawSnapshot(string SnapshotId, SnapshotManifest Manifest, string? SnapshotDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(SnapshotId))
        {
            throw new ArgumentException("A raw snapshot identifier is required.", nameof(SnapshotId));
        }

        ArgumentNullException.ThrowIfNull(Manifest);
        if (SnapshotDirectory is not null
            && (string.IsNullOrWhiteSpace(SnapshotDirectory) || !Path.IsPathFullyQualified(SnapshotDirectory)))
        {
            throw new ArgumentException("The optional raw snapshot directory must be absolute.", nameof(SnapshotDirectory));
        }

        this.SnapshotId = SnapshotId;
        this.Manifest = Manifest;
        this.SnapshotDirectoryOverride = SnapshotDirectory is null ? null : Path.GetFullPath(SnapshotDirectory);
    }

    public string SnapshotId { get; }

    public SnapshotManifest Manifest { get; }

    /// <summary>
    /// Gets the directory containing the raw snapshot files. A caller may
    /// override the path recorded in a portable manifest when the snapshot
    /// directory has been moved since the manifest was created.
    /// </summary>
    public string SnapshotDirectory => SnapshotDirectoryOverride ?? Manifest.SnapshotDirectory;

    public string? SnapshotDirectoryOverride { get; }
}

public sealed record MaterializationOptions
{
    public MaterializationOptions(string OutputDirectory, string? KeyFile = null)
    {
        if (string.IsNullOrWhiteSpace(OutputDirectory) || !Path.IsPathFullyQualified(OutputDirectory))
        {
            throw new ArgumentException("The materialization output directory must be absolute.", nameof(OutputDirectory));
        }

        if (KeyFile is not null && (string.IsNullOrWhiteSpace(KeyFile) || !Path.IsPathFullyQualified(KeyFile)))
        {
            throw new ArgumentException("The optional key file must be an absolute path.", nameof(KeyFile));
        }

        this.OutputDirectory = Path.GetFullPath(OutputDirectory);
        this.KeyFile = KeyFile is null ? null : Path.GetFullPath(KeyFile);
    }

    public string OutputDirectory { get; }

    public string? KeyFile { get; }
}

public sealed record MaterializedDatabase(
    string LogicalRole,
    int? ShardNumber,
    string DatabasePath,
    string Sha256,
    long ByteLength);

public sealed record DecryptedWorkspace
{
    public DecryptedWorkspace(
        string WorkspaceId,
        string SourceSnapshotId,
        string BackendId,
        string BackendVersion,
        IReadOnlyList<MaterializedDatabase> Databases)
    {
        if (string.IsNullOrWhiteSpace(WorkspaceId))
        {
            throw new ArgumentException("A decrypted workspace identifier is required.", nameof(WorkspaceId));
        }

        if (string.IsNullOrWhiteSpace(SourceSnapshotId))
        {
            throw new ArgumentException("A source snapshot identifier is required.", nameof(SourceSnapshotId));
        }

        if (string.IsNullOrWhiteSpace(BackendId))
        {
            throw new ArgumentException("A materializer backend identifier is required.", nameof(BackendId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(BackendVersion);
        ArgumentNullException.ThrowIfNull(Databases);
        this.WorkspaceId = WorkspaceId;
        this.SourceSnapshotId = SourceSnapshotId;
        this.BackendId = BackendId;
        this.BackendVersion = BackendVersion;
        this.Databases = new ReadOnlyCollection<MaterializedDatabase>(Databases.ToArray());
    }

    public string WorkspaceId { get; }

    public string SourceSnapshotId { get; }

    public string BackendId { get; }

    public string BackendVersion { get; }

    public IReadOnlyList<MaterializedDatabase> Databases { get; }
}
