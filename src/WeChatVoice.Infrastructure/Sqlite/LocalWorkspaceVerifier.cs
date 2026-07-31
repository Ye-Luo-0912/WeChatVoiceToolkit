using System.Text.Json;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Serialization;

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

        await VerifyMaterializationProvenanceAsync(workspace, sourceRoot, cancellationToken).ConfigureAwait(false);

        return new VerifiedLocalWorkspace(workspace, DateTimeOffset.UtcNow);
    }

    private static async Task VerifyMaterializationProvenanceAsync(
        LocalWorkspace workspace,
        string sourceRoot,
        CancellationToken cancellationToken)
    {
        if (workspace.Provenance is null)
        {
            return;
        }

        var manifestPath = CombineUnderRoot(sourceRoot, ".wechatvoice/materialization-manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new WorkspaceVerificationException("The materialization manifest referenced by the workspace is missing.");
        }

        EnsureNoReparsePointsOnPath(sourceRoot, manifestPath);

        var manifestHash = await FileHashing.ComputeSha256Async(manifestPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(manifestHash, workspace.Provenance.MaterializationManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkspaceVerificationException("The materialization manifest hash no longer matches workspace provenance.");
        }

        await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var manifest = await JsonSerializer.DeserializeAsync<MaterializationManifest>(stream, InfrastructureJson.Compact, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkspaceVerificationException("The materialization manifest is empty.");
        var provenance = workspace.Provenance;
        if (!string.Equals(manifest.WorkspaceId, provenance.MaterializationId, StringComparison.Ordinal)
            || !string.Equals(manifest.SourceSnapshotId, provenance.SourceSnapshotId, StringComparison.Ordinal)
            || !string.Equals(manifest.BackendId, provenance.BackendId, StringComparison.Ordinal)
            || !string.Equals(manifest.BackendVersion, provenance.BackendVersion, StringComparison.Ordinal)
            || !string.Equals(manifest.BackendSha256, provenance.BackendBundleSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkspaceVerificationException("The materialization manifest provenance does not match the workspace.");
        }

        var verifiedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var outputPath = CombineUnderRoot(sourceRoot, file.OutputRelativePath);
            EnsureNoReparsePointsOnPath(sourceRoot, outputPath);
            if (!File.Exists(outputPath))
            {
                throw new WorkspaceVerificationException($"A materialization output file is missing: '{file.OutputRelativePath}'.");
            }

            var info = new FileInfo(outputPath);
            var hash = await FileHashing.ComputeSha256Async(outputPath, cancellationToken).ConfigureAwait(false);
            if (info.Length != file.ByteLength || !string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new WorkspaceVerificationException($"A materialization output file changed: '{file.OutputRelativePath}'.");
            }

            if (!verifiedFiles.Add(file.OutputRelativePath.Replace('\\', '/')))
            {
                throw new WorkspaceVerificationException($"The materialization manifest contains a duplicate output file: '{file.OutputRelativePath}'.");
            }
        }

        foreach (var database in manifest.Databases.Where(static item => item.Status is MaterializationDatabaseStatus.Materialized or MaterializationDatabaseStatus.CopiedAsPlaintext))
        {
            var outputPath = CombineUnderRoot(sourceRoot, database.OutputRelativePath);
            if (!verifiedFiles.Contains(database.OutputRelativePath.Replace('\\', '/')))
            {
                throw new WorkspaceVerificationException($"The materialization manifest does not cover database output '{database.OutputRelativePath}'.");
            }
        }
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
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new WorkspaceVerificationException($"Workspace output path is not relative: '{relativePath}'.");
        }

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
