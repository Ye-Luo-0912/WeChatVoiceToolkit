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
    public MaterializationOptions(string OutputDirectory, TimeSpan? BackendTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(OutputDirectory) || !Path.IsPathFullyQualified(OutputDirectory))
        {
            throw new ArgumentException("The materialization output directory must be absolute.", nameof(OutputDirectory));
        }

        this.OutputDirectory = Path.GetFullPath(OutputDirectory);
        this.BackendTimeout = BackendTimeout ?? TimeSpan.FromMinutes(5);
        if (this.BackendTimeout <= TimeSpan.Zero || this.BackendTimeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(BackendTimeout), "The backend timeout must be greater than zero and no more than one hour.");
        }
    }

    public string OutputDirectory { get; }

    public TimeSpan BackendTimeout { get; }
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
    string? Error = null,
    string? EncryptionProfileId = null);

public sealed record MaterializationFile(
    string OutputRelativePath,
    string Sha256,
    long ByteLength);

public sealed record MaterializationManifest
{
    public MaterializationManifest(
        string WorkspaceId,
        string SourceSnapshotId,
        string BackendId,
        string BackendVersion,
        string BackendSha256,
        IReadOnlyList<MaterializedDatabase> Databases,
        IReadOnlyList<MaterializationFile> Files,
        string? KeyExtractionProfileId = null,
        string? ProcessVersion = null,
        string? ProcessImageSha256 = null,
        string? WcdbModuleSha256 = null,
        string? AccountSidFingerprint = null,
        string? AccountId = null,
        AccountEvidenceState AccountEvidenceState = AccountEvidenceState.Unknown,
        UserConfirmationState UserConfirmationState = UserConfirmationState.NotConfirmed,
        string? ConfirmedAccountId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(SourceSnapshotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(BackendId);
        ArgumentException.ThrowIfNullOrWhiteSpace(BackendVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(BackendSha256);
        ArgumentNullException.ThrowIfNull(Databases);
        ArgumentNullException.ThrowIfNull(Files);
        this.WorkspaceId = WorkspaceId;
        this.SourceSnapshotId = SourceSnapshotId;
        this.BackendId = BackendId;
        this.BackendVersion = BackendVersion;
        this.BackendSha256 = BackendSha256;
        this.Databases = new ReadOnlyCollection<MaterializedDatabase>(Databases.ToArray());
        this.Files = new ReadOnlyCollection<MaterializationFile>(Files.ToArray());
        this.KeyExtractionProfileId = KeyExtractionProfileId;
        this.ProcessVersion = ProcessVersion;
        this.ProcessImageSha256 = ProcessImageSha256;
        this.WcdbModuleSha256 = WcdbModuleSha256;
        this.AccountSidFingerprint = AccountSidFingerprint;
        this.AccountId = string.IsNullOrWhiteSpace(AccountId) ? null : AccountId;
        this.AccountEvidenceState = AccountEvidenceState == WeChatVoice.Core.Models.AccountEvidenceState.Unknown && this.AccountId is not null
            ? WeChatVoice.Core.Models.AccountEvidenceState.PathCandidate
            : AccountEvidenceState;
        this.UserConfirmationState = UserConfirmationState;
        this.ConfirmedAccountId = string.IsNullOrWhiteSpace(ConfirmedAccountId) ? null : ConfirmedAccountId;
    }

    public string WorkspaceId { get; }
    public string SourceSnapshotId { get; }
    public string BackendId { get; }
    public string BackendVersion { get; }
    public string BackendSha256 { get; }
    public IReadOnlyList<MaterializedDatabase> Databases { get; }
    public IReadOnlyList<MaterializationFile> Files { get; }
    public string? KeyExtractionProfileId { get; }
    public string? ProcessVersion { get; }
    public string? ProcessImageSha256 { get; }
    public string? WcdbModuleSha256 { get; }
    public string? AccountSidFingerprint { get; }
    public string? AccountId { get; }
    public AccountEvidenceState AccountEvidenceState { get; }
    public UserConfirmationState UserConfirmationState { get; }
    public string? ConfirmedAccountId { get; }
}

public static class MaterializationCommitStates
{
    public const string Staging = "Staging";
    public const string DatabasesCommitted = "DatabasesCommitted";
    public const string WorkspaceCommitted = "WorkspaceCommitted";
    public const string Completed = "Completed";
    public const string FailedRecoverable = "FailedRecoverable";

    public static bool IsKnown(string? state)
        => state is Staging or DatabasesCommitted or WorkspaceCommitted or Completed or FailedRecoverable;
}

public sealed record MaterializationStateDocument(
    string State,
    DateTimeOffset UpdatedAtUtc,
    string? FailureCode = null,
    string? OperationId = null);

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
        string ManifestPath,
        string? KeyExtractionProfileId = null,
        string? ProcessVersion = null,
        string? ProcessImageSha256 = null,
        string? WcdbModuleSha256 = null,
        string? AccountSidFingerprint = null)
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
        this.KeyExtractionProfileId = KeyExtractionProfileId;
        this.ProcessVersion = ProcessVersion;
        this.ProcessImageSha256 = ProcessImageSha256;
        this.WcdbModuleSha256 = WcdbModuleSha256;
        this.AccountSidFingerprint = AccountSidFingerprint;
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

    public string? KeyExtractionProfileId { get; }

    public string? ProcessVersion { get; }

    public string? ProcessImageSha256 { get; }

    public string? WcdbModuleSha256 { get; }

    public string? AccountSidFingerprint { get; }
}
