using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;

namespace WeChatVoice.Infrastructure.Sqlite;

/// <summary>
/// Rebinds no paths and trusts no persisted hashes. It re-enumerates the root,
/// rejects every reparse point, and compares a fresh DB/WAL/SHM probe with the
/// workspace document before an adapter can open it.
/// </summary>
public sealed class LocalWorkspaceVerifier : ILocalWorkspaceVerifier
{
    private readonly DataSetProbeService _probeService;

    public LocalWorkspaceVerifier(DataSetProbeService? probeService = null)
        => _probeService = probeService ?? new DataSetProbeService();

    public async Task<VerifiedLocalWorkspace> VerifyAsync(LocalWorkspace workspace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        cancellationToken.ThrowIfCancellationRequested();

        var sourceRoot = Path.GetFullPath(workspace.SourceRoot);
        if (!Directory.Exists(sourceRoot))
        {
            throw new WorkspaceVerificationException($"The workspace source root does not exist: '{sourceRoot}'.");
        }

        EnsureNoReparsePoints(sourceRoot);
        foreach (var artifact in workspace.DataSet.Databases)
        {
            if (string.IsNullOrWhiteSpace(artifact.LocalPath) || Path.IsPathRooted(artifact.DatabasePath))
            {
                throw new WorkspaceVerificationException($"Workspace artifact '{artifact.DatabasePath}' does not contain a safe relative database path.");
            }

            var expectedPath = CombineUnderRoot(sourceRoot, artifact.DatabasePath);
            var actualPath = Path.GetFullPath(artifact.LocalPath);
            if (!string.Equals(expectedPath, actualPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new WorkspaceVerificationException($"Workspace artifact path escapes or disagrees with SourceRoot: '{artifact.DatabasePath}'.");
            }

            if (!File.Exists(actualPath))
            {
                throw new WorkspaceVerificationException($"Workspace database file is missing: '{artifact.DatabasePath}'.");
            }

            EnsureNoReparsePointsOnPath(sourceRoot, actualPath);
        }

        var probe = await _probeService.ProbeAsync(
            sourceRoot,
            new DataSetProbeOptions(IncludeLocalPaths: true),
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(workspace.DataSet.DataSetId, probe.DataSet.DataSetId, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkspaceVerificationException($"Workspace DatasetId '{workspace.DataSet.DataSetId}' no longer matches current content '{probe.DataSet.DataSetId}'.");
        }

        var expected = workspace.DataSet.Databases.ToDictionary(static item => item.DatabasePath, StringComparer.OrdinalIgnoreCase);
        var actual = probe.DataSet.Databases.ToDictionary(static item => item.DatabasePath, StringComparer.OrdinalIgnoreCase);
        if (expected.Count != actual.Count || expected.Keys.Except(actual.Keys, StringComparer.OrdinalIgnoreCase).Any())
        {
            throw new WorkspaceVerificationException("Workspace database file set no longer matches the current source root.");
        }

        foreach (var pair in expected)
        {
            var current = actual[pair.Key];
            var saved = pair.Value;
            if (!string.Equals(saved.LogicalRole, current.LogicalRole, StringComparison.OrdinalIgnoreCase)
                || saved.ShardNumber != current.ShardNumber
                || saved.MainLength != current.MainLength
                || !string.Equals(saved.MainSha256, current.MainSha256, StringComparison.OrdinalIgnoreCase)
                || saved.WalLength != current.WalLength
                || !string.Equals(saved.WalSha256, current.WalSha256, StringComparison.OrdinalIgnoreCase)
                || saved.ShmLength != current.ShmLength
                || !string.Equals(saved.ShmSha256, current.ShmSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(saved.DatabaseGroupFingerprint, current.DatabaseGroupFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                throw new WorkspaceVerificationException($"Workspace database group content changed: '{pair.Key}'.");
            }
        }

        return new VerifiedLocalWorkspace(workspace, DateTimeOffset.UtcNow);
    }

    private static void EnsureNoReparsePoints(string root)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new WorkspaceVerificationException($"Reparse points are not allowed in a local workspace: '{root}'.");
        }

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.OrdinalIgnoreCase))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new WorkspaceVerificationException($"Reparse points are not allowed in a local workspace: '{entry}'.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }
    }

    private static void EnsureNoReparsePointsOnPath(string root, string path)
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(path)!);
        while (current is not null && !string.Equals(current.FullName, root, StringComparison.OrdinalIgnoreCase))
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new WorkspaceVerificationException($"A workspace database path traverses a reparse point: '{current.FullName}'.");
            }

            current = current.Parent;
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new WorkspaceVerificationException($"A workspace database file is a reparse point: '{path}'.");
        }
    }

    private static string CombineUnderRoot(string root, string relativePath)
    {
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkspaceVerificationException($"Workspace path is outside SourceRoot: '{relativePath}'.");
        }

        return candidate;
    }
}

public sealed class WorkspaceVerificationException : IOException
{
    public WorkspaceVerificationException(string message)
        : base(message)
    {
    }
}
