using System.Collections.Concurrent;
using WeChatVoice.Core.Ports;

namespace WeChatVoice.Application;

/// <summary>
/// Instance-owned retry queue for temporary-file cleanup. The queue retains
/// the private path only in memory; snapshots contain category/code/type and
/// never disclose local paths or source identities.
/// </summary>
public sealed class TemporaryFileCleanupQueue : ITemporaryFileCleanupQueue
{
    private readonly ConcurrentQueue<PendingCleanup> _pending = new();

    public void Enqueue(string absolutePath, CleanupDiagnostic diagnostic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (!Path.IsPathFullyQualified(absolutePath))
        {
            throw new ArgumentException("Cleanup paths must be absolute.", nameof(absolutePath));
        }

        _pending.Enqueue(new PendingCleanup(absolutePath, diagnostic));
    }

    public IReadOnlyList<CleanupDiagnostic> GetSnapshot()
        => _pending.Select(static item => item.Diagnostic).ToArray();

    public ValueTask RetryPendingAsync(CancellationToken cancellationToken)
    {
        var attempts = _pending.Count;
        for (var index = 0; index < attempts; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_pending.TryDequeue(out var pending))
            {
                break;
            }

            if (!TryDelete(pending.Path))
            {
                _pending.Enqueue(pending);
            }
        }

        return ValueTask.CompletedTask;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return true;
            }

            File.Delete(path);
            return !File.Exists(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record PendingCleanup(string Path, CleanupDiagnostic Diagnostic);
}
