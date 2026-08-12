using WeChatVoice.Core.Ports;

namespace WeChatVoice.Infrastructure.Storage;

/// <summary>
/// Best-effort cleanup of stale, application-owned temporary staging on host
/// startup. It only removes objects under well-known app roots that are older
/// than a cutoff, refuses reparse points, and never touches workspace documents,
/// materialized roots, exports, or datasets. Failures are queued for a later
/// best-effort retry instead of being surfaced as startup errors.
/// </summary>
public sealed class StartupOrphanSweeper
{
    private static readonly TimeSpan DefaultOlderThan = TimeSpan.FromHours(24);
    private static readonly string StagingSuffix = ".staging";

    private readonly StorageRoots _roots;
    private readonly string? _decoderTempRoot;
    private readonly string? _durationTempRoot;

    public StartupOrphanSweeper(StorageRoots roots)
        : this(roots, decoderTempRoot: null, durationTempRoot: null)
    {
    }

    public StartupOrphanSweeper(
        StorageRoots roots,
        string? decoderTempRoot,
        string? durationTempRoot)
    {
        _roots = roots;
        _decoderTempRoot = decoderTempRoot;
        _durationTempRoot = durationTempRoot;
    }

    /// <summary>
    /// Sweeps stale snapshot staging directories and stale decoder/duration
    /// temporary payloads. Returns the number of objects removed; a skipped
    /// reparse point or a queued delete failure is not counted as removed.
    /// </summary>
    public int Sweep(ITemporaryFileCleanupQueue? cleanupQueue = null, TimeSpan? olderThan = null)
    {
        var cutoff = DateTime.UtcNow - (olderThan ?? DefaultOlderThan);
        var removed = 0;

        removed += SweepStagingUnderRoot(_roots.SnapshotsRoot, cutoff, cleanupQueue);
        if (_roots.TempRoot is not null)
        {
            removed += SweepStagingUnderRoot(Path.Combine(_roots.TempRoot, "Snapshots"), cutoff, cleanupQueue);
            removed += SweepStagingUnderRoot(Path.Combine(_roots.TempRoot, "SnapshotsStaging"), cutoff, cleanupQueue);
        }

        removed += SweepStaleFiles(DecoderTempRoot(), "*.input.silk", cutoff, cleanupQueue);
        removed += SweepStaleFiles(DecoderTempRoot(), "*.output.wav", cutoff, cleanupQueue);
        removed += SweepStaleFiles(DurationTempRoot(), "*.wav", cutoff, cleanupQueue);

        return removed;
    }

    private string? DecoderTempRoot() => _decoderTempRoot ?? Path.Combine(Path.GetTempPath(), "wechatvoice-decoder");

    private string? DurationTempRoot() => _durationTempRoot ?? Path.Combine(Path.GetTempPath(), "wechatvoice-duration");

    /// <summary>
    /// Removes stale `.staging` directories that are direct children of a known
    /// app root. The staging naming protocol is checked so an unrelated directory
    /// is never removed, and reparse points are always refused.
    /// </summary>
    private int SweepStagingUnderRoot(string root, DateTime cutoff, ITemporaryFileCleanupQueue? cleanupQueue)
    {
        if (!Directory.Exists(root) || IsReparsePoint(root))
        {
            return 0;
        }

        var removed = 0;
        // Snapshot staging is a sibling of a canonical operation under an account
        // fingerprint directory (Snapshots/&lt;fingerprint&gt;/&lt;.operation...staging&gt;).
        // Sweep both the direct children and the nested account directories.
        foreach (var candidate in Directory.EnumerateDirectories(root))
        {
            if (IsReparsePoint(candidate))
            {
                continue;
            }

            if (IsStagingDirectory(candidate))
            {
                removed += RemoveStaleDirectory(candidate, cutoff, cleanupQueue);
                continue;
            }

            // One level of nesting for account fingerprint directories.
            foreach (var nested in Directory.EnumerateDirectories(candidate))
            {
                if (!IsReparsePoint(nested) && IsStagingDirectory(nested))
                {
                    removed += RemoveStaleDirectory(nested, cutoff, cleanupQueue);
                }
            }
        }

        return removed;
    }

    private static bool IsStagingDirectory(string path)
        => Path.GetFileName(path).EndsWith(StagingSuffix, StringComparison.Ordinal);

    private int RemoveStaleDirectory(string candidate, DateTime cutoff, ITemporaryFileCleanupQueue? cleanupQueue)
    {
        try
        {
            if (File.GetLastWriteTimeUtc(candidate) > cutoff)
            {
                return 0;
            }

            if (TryDeleteDirectory(candidate))
            {
                return 1;
            }

            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            cleanupQueue?.Enqueue(candidate, new CleanupDiagnostic("startup-staging", "orphan-delete-failed", exception.GetType().Name));
            return 0;
        }
    }

    private int SweepStaleFiles(string? root, string pattern, DateTime cutoff, ITemporaryFileCleanupQueue? cleanupQueue)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) || IsReparsePoint(root))
        {
            return 0;
        }

        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (IsReparsePoint(path) || File.GetLastWriteTimeUtc(path) > cutoff)
                {
                    continue;
                }

                File.Delete(path);
                if (!File.Exists(path))
                {
                    removed++;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                cleanupQueue?.Enqueue(path, new CleanupDiagnostic("startup-temp", "orphan-delete-failed", exception.GetType().Name));
            }
        }

        return removed;
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return true;
            }

            Directory.Delete(path, recursive: true);
            return !Directory.Exists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
