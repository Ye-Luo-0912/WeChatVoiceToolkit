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

    public string CreateDefault(string snapshotDirectory, string? snapshotId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotDirectory);

        var identity = string.IsNullOrWhiteSpace(snapshotId)
            ? Path.GetFullPath(snapshotDirectory)
            : snapshotId.Trim();
        var fingerprint = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes("wechatvoice-workspace-v1|" + identity)))
            .ToLowerInvariant()[..32];
        var root = Path.Combine(_applicationDataRoot, "Data", "Workspaces");
        var candidate = Path.Combine(root, fingerprint);

        if (IsSafeCandidate(snapshotDirectory, candidate))
        {
            return candidate;
        }

        // A previous failed or completed run may occupy the deterministic
        // location. Keep the automatic flow usable without touching it; the
        // recovery action can still be used when the existing state is shown.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var fallback = Path.Combine(root, fingerprint + "-" + Guid.NewGuid().ToString("N"));
            if (IsSafeCandidate(snapshotDirectory, fallback))
            {
                return fallback;
            }
        }

        throw new InvalidOperationException("无法分配安全的 Workspace 输出目录。");
    }

    private static bool IsSafeCandidate(string snapshotDirectory, string candidate)
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
