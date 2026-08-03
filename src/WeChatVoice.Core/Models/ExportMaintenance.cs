namespace WeChatVoice.Core.Models;

/// <summary>
/// A machine-readable finding produced by an export verification pass.  Paths
/// are export-relative and never contain source database or contact identity.
/// </summary>
public sealed record ExportVerificationIssue(
    string Code,
    string? RelativePath,
    string Detail);

public sealed record ExportVerificationResult(
    string ExportDirectory,
    string? RunId,
    bool IsValid,
    string? ManifestSha256,
    int VerifiedOriginalCount,
    int MissingFileCount,
    int ExtraFileCount,
    bool JournalCommitted,
    bool CsvConsistent,
    bool TrainingSelectionConsistent,
    IReadOnlyList<ExportVerificationIssue> Issues);

public sealed record ExportRepairResult(
    ExportVerificationResult Verification,
    string JournalPath,
    bool OriginalArtifactsChanged = false)
{
    public string ExportDirectory => Verification.ExportDirectory;

    public string? RunId => Verification.RunId;
}
