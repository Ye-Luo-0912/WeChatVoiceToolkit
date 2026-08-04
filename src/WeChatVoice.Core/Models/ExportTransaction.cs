namespace WeChatVoice.Core.Models;

/// <summary>
/// Durable states of one export run. The state is intentionally monotonic
/// during normal operation; recovery may only advance a partially published
/// run after validating every recorded artifact.
/// </summary>
public enum ExportTransactionState
{
    Staging,
    Prepared,
    Publishing,
    ArtifactsCommitted,
    MetadataCommitted,
    Completed,
    FailedRecoverable,
    RolledBack,
}

public enum ExportPublishState
{
    NotStarted,
    Publishing,
    Committed,
    Existing,
    Failed,
}

public enum ExportCompletionPolicy
{
    /// <summary>Every prepared eligible item must succeed or the run is rolled back.</summary>
    ExactAllOrNothing,

    /// <summary>Successful items may be committed while failures remain in the manifest.</summary>
    BestEffort,
}

public enum ExportTransactionItemState
{
    Created,
    PayloadCommitted,
    EntryBound,
    ReadyToPublish,
    Publishing,
    Published,
}

/// <summary>
/// Hash bindings for every derived metadata product. The descriptor is the
/// single durable commit boundary for a run's manifests, CSV, and index.
/// </summary>
public sealed record ExportMetadataCommitDescriptor(
    string RunId,
    string PrivateManifestSha256,
    string PortableManifestSha256,
    string DatasetCsvSha256,
    string ArtifactIndexSha256,
    string? SelectionProfileSha256 = null,
    string Format = "wechatvoice-metadata-commit-v1");

/// <summary>
/// Private, crash-recovery-only description of one staged artifact. It is
/// never copied into the portable dataset manifest.
/// </summary>
public sealed record ExportTransactionItem(
    string MessageId,
    string? SourceStableKey,
    string StagedOriginalPath,
    string FinalOriginalPath,
    string? StagedDecodedPath,
    string? FinalDecodedPath,
    long? OriginalByteLength,
    string? OriginalSha256,
    long? DecodedByteLength,
    string? DecodedSha256,
    ExportPublishState OriginalPublishState,
    ExportPublishState DecodedPublishState,
    ExportArtifactState PreviousOriginalState,
    ExportArtifactState PreviousDecodedState,
    VoiceExportEntry? Entry = null,
    string? TransactionKey = null,
    ExportTransactionItemState ItemState = ExportTransactionItemState.Created);

public static class ExportItemTransactionKey
{
    public static string Compute(string sourceStableKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceStableKey);
        return Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sourceStableKey)))
            .ToLowerInvariant();
    }

    public static string? TryCompute(string? sourceStableKey)
        => string.IsNullOrWhiteSpace(sourceStableKey) ? null : Compute(sourceStableKey);
}

public sealed record ExportTransactionDocument
{
    public ExportTransactionDocument(
        string RunId,
        string OperationId,
        string? SelectionFingerprint,
        ExportTransactionState State,
        DateTimeOffset UpdatedAtUtc,
        IReadOnlyList<ExportTransactionItem>? Items = null,
        ExportMetadataCommitDescriptor? MetadataCommit = null,
        string? FailureCode = null,
        string Format = "wechatvoice-export-transaction-v1",
        bool ExplicitRollback = false)
    {
        this.RunId = RunId;
        this.OperationId = OperationId;
        this.SelectionFingerprint = SelectionFingerprint;
        this.State = State;
        this.UpdatedAtUtc = UpdatedAtUtc.ToUniversalTime();
        this.Items = Items ?? Array.Empty<ExportTransactionItem>();
        this.MetadataCommit = MetadataCommit;
        this.FailureCode = FailureCode;
        this.Format = Format;
        this.ExplicitRollback = ExplicitRollback;
    }

    public string RunId { get; }
    public string OperationId { get; }
    public string? SelectionFingerprint { get; }
    public ExportTransactionState State { get; }
    public DateTimeOffset UpdatedAtUtc { get; }
    public IReadOnlyList<ExportTransactionItem> Items { get; }
    public ExportMetadataCommitDescriptor? MetadataCommit { get; }
    public string? FailureCode { get; }
    public string Format { get; }
    public bool ExplicitRollback { get; }
}
