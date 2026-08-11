using System.Security.Cryptography;
using System.Text;
using WeChatVoice.Core.Models;

namespace WeChatVoice.Desktop.Infrastructure;

/// <summary>
/// Allocates an opaque, application-owned materialization destination. The
/// destination is derived from the snapshot content id when available, never
/// from a user-facing source path or account name.
/// </summary>
public sealed class WorkspaceOutputDirectoryFactory
{
    private readonly string _applicationDataRoot;

    public WorkspaceOutputDirectoryFactory(string applicationDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataRoot);
        _applicationDataRoot = Path.GetFullPath(applicationDataRoot);
    }

    /// <summary>
    /// Returns the deterministic canonical destination for a snapshot. It is
    /// deliberately returned even when a previous run already occupies it: the
    /// caller must inspect that state (via <c>ProjectStateWorkflow</c>) and
    /// verify/reuse/recover it instead of silently creating a new GUID copy.
    /// </summary>
    public string CreateDefault(string snapshotDirectory, string? snapshotId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotDirectory);
        var candidate = ComputeCanonical(snapshotDirectory, snapshotId);
        EnsureCanonicalUsable(snapshotDirectory, candidate);
        return candidate;
    }

    /// <summary>
    /// Explicitly allocates a separate, empty copy (canonical + guid). Only used
    /// when the user explicitly asks to start a new workspace instead of reusing
    /// the existing canonical one.
    /// </summary>
    public string CreateNewCopy(string snapshotDirectory, string? snapshotId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotDirectory);
        var canonical = ComputeCanonical(snapshotDirectory, snapshotId);
        var root = Path.GetDirectoryName(canonical)
            ?? throw new InvalidOperationException("无法确定 Workspace 输出目录的父目录。");
        var name = Path.GetFileName(canonical);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var fallback = Path.Combine(root, $"{name}-{Guid.NewGuid():N}");
            if (IsEmptyAllocation(snapshotDirectory, fallback))
            {
                return fallback;
            }
        }

        throw new InvalidOperationException("无法分配安全的 Workspace 副本目录。");
    }

    /// <summary>
    /// True when <paramref name="outputDirectory"/> already holds app-owned
    /// materialization output (a completed/recoverable/staging workspace).
    /// </summary>
    public static bool IsOccupied(string outputDirectory)
    {
        try
        {
            return Directory.Exists(outputDirectory)
                && Directory.EnumerateFileSystemEntries(outputDirectory).Any();
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private string ComputeCanonical(string snapshotDirectory, string? snapshotId)
    {
        var identity = string.IsNullOrWhiteSpace(snapshotId)
            ? Path.GetFullPath(snapshotDirectory)
            : snapshotId.Trim();
        var fingerprint = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes("wechatvoice-workspace-v1|" + identity)))
            .ToLowerInvariant()[..32];
        return Path.Combine(_applicationDataRoot, "Data", "Workspaces", fingerprint);
    }

    private static void EnsureCanonicalUsable(string snapshotDirectory, string candidate)
    {
        try
        {
            PathOverlapGuard.EnsureDisjoint(snapshotDirectory, candidate);
            if (File.Exists(candidate))
            {
                throw new InvalidOperationException("Workspace 输出路径已经是文件。");
            }

            if (Directory.Exists(candidate)
                && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Workspace 输出目录包含 Reparse Point。");
            }
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("无法分配安全的 Workspace 输出目录。");
        }
        catch (IOException)
        {
            throw new InvalidOperationException("无法分配安全的 Workspace 输出目录。");
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException("无法分配安全的 Workspace 输出目录。");
        }
        catch (InvalidDataException)
        {
            throw new InvalidOperationException("无法分配安全的 Workspace 输出目录。");
        }
    }

    private static bool IsEmptyAllocation(string snapshotDirectory, string candidate)
    {
        try
        {
            if (Directory.Exists(snapshotDirectory))
            {
                return DesktopPathValidator.ValidateSnapshotPaths(snapshotDirectory, candidate).IsValid;
            }

            // The UI may receive a manually entered path before the workflow
            // verifies it. Do not reject the form prematurely; still enforce
            // path non-overlap and refuse an existing non-empty destination.
            PathOverlapGuard.EnsureDisjoint(snapshotDirectory, candidate);
            if (File.Exists(candidate))
            {
                return false;
            }

            return !Directory.Exists(candidate)
                || !Directory.EnumerateFileSystemEntries(candidate).Any();
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    public static string CreateWorkspaceDocumentPath(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var fullOutput = Path.GetFullPath(outputDirectory);
        return Path.Combine(
            Path.GetDirectoryName(fullOutput) ?? throw new ArgumentException("Workspace 输出目录必须有父目录。", nameof(outputDirectory)),
            Path.GetFileName(fullOutput) + ".workspace.json");
    }
}
