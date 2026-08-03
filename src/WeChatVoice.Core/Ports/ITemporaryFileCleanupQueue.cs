namespace WeChatVoice.Core.Ports;

/// <summary>
/// Receives best-effort cleanup failures for sensitive temporary files without
/// exposing their paths to logs or host UI. Implementations may retry the
/// private paths later in the owning composition-root lifetime.
/// </summary>
public interface ITemporaryFileCleanupQueue
{
    void Enqueue(string absolutePath, CleanupDiagnostic diagnostic);

    IReadOnlyList<CleanupDiagnostic> GetSnapshot();

    ValueTask RetryPendingAsync(CancellationToken cancellationToken);
}

public sealed record CleanupDiagnostic(
    string ResourceKind,
    string Code,
    string ExceptionType);
