using System.Collections.ObjectModel;

namespace WeChatVoice.Core.Models;

public sealed record RawSnapshot
{
    public RawSnapshot(SnapshotManifest Manifest, string? SnapshotDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(Manifest);
        if (SnapshotDirectory is not null
            && (string.IsNullOrWhiteSpace(SnapshotDirectory) || !Path.IsPathFullyQualified(SnapshotDirectory)))
        {
            throw new ArgumentException("The optional raw snapshot directory must be absolute.", nameof(SnapshotDirectory));
        }

        this.SnapshotId = Manifest.SnapshotId;
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
    public MaterializationOptions(string OutputDirectory)
    {
        if (string.IsNullOrWhiteSpace(OutputDirectory) || !Path.IsPathFullyQualified(OutputDirectory))
        {
            throw new ArgumentException("The materialization output directory must be absolute.", nameof(OutputDirectory));
        }

        this.OutputDirectory = Path.GetFullPath(OutputDirectory);
    }

    public string OutputDirectory { get; }
}

public enum MaterializationDatabaseStatus
{
    Materialized,
    CopiedAsPlaintext,
    IntentionallyIgnored,
    Failed,
}

public sealed record MaterializedDatabase(
    string SourceRelativePath,
    string SourceGroupFingerprint,
    string OutputRelativePath,
    string LogicalRole,
    int? ShardNumber,
    string Sha256,
    long ByteLength,
    string SchemaFingerprint,
    MaterializationDatabaseStatus Status = MaterializationDatabaseStatus.Materialized,
    string? Error = null);

public sealed record MaterializationFile(
    string OutputRelativePath,
    string Sha256,
    long ByteLength);

public sealed record MaterializationManifest(
    string WorkspaceId,
    string SourceSnapshotId,
    string BackendId,
    string BackendVersion,
    string BackendSha256,
    IReadOnlyList<MaterializedDatabase> Databases,
    IReadOnlyList<MaterializationFile> Files);

/// <summary>
/// Fixed output contract emitted by a materialization backend. The backend,
/// rather than the host, owns the source-to-output relationship. This avoids
/// filename heuristics and makes ambiguous or silently missing databases a
/// hard failure.
/// </summary>
public sealed record MaterializationOutputManifest(
    int FormatVersion,
    IReadOnlyList<MaterializationOutputDatabase> Databases)
{
    public const int CurrentFormatVersion = 1;
}

public sealed record MaterializationOutputDatabase(
    string SourceRelativePath,
    string OutputRelativePath,
    MaterializationDatabaseStatus Status = MaterializationDatabaseStatus.Materialized,
    string? Error = null);

public sealed record MaterializationResult
{
    public MaterializationResult(
        string WorkspaceId,
        string SourceSnapshotId,
        string BackendId,
        string BackendVersion,
        string BackendSha256,
        string OutputRoot,
        IReadOnlyList<MaterializedDatabase> Databases,
        IReadOnlyList<MaterializationFile> Files,
        string ManifestPath)
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
        ArgumentException.ThrowIfNullOrWhiteSpace(BackendSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(OutputRoot);
        ArgumentNullException.ThrowIfNull(Databases);
        ArgumentNullException.ThrowIfNull(Files);
        ArgumentException.ThrowIfNullOrWhiteSpace(ManifestPath);
        this.WorkspaceId = WorkspaceId;
        this.SourceSnapshotId = SourceSnapshotId;
        this.BackendId = BackendId;
        this.BackendVersion = BackendVersion;
        this.BackendSha256 = BackendSha256;
        this.OutputRoot = Path.GetFullPath(OutputRoot);
        this.Databases = new ReadOnlyCollection<MaterializedDatabase>(Databases.ToArray());
        this.Files = new ReadOnlyCollection<MaterializationFile>(Files.ToArray());
        this.ManifestPath = Path.GetFullPath(ManifestPath);
    }

    public string WorkspaceId { get; }

    public string SourceSnapshotId { get; }

    public string BackendId { get; }

    public string BackendVersion { get; }

    public string BackendSha256 { get; }

    public string OutputRoot { get; }

    public IReadOnlyList<MaterializedDatabase> Databases { get; }

    public IReadOnlyList<MaterializationFile> Files { get; }

    public string ManifestPath { get; }
}
