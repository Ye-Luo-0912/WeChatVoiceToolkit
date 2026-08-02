using System.Text.Json;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Serialization;
using WeChatVoice.Infrastructure.Sqlite;

namespace WeChatVoice.Infrastructure.Materialization;

/// <summary>
/// Adopts a database bundle whose materialization committed but whose local
/// workspace document was not committed. Recovery revalidates the state,
/// manifest, every listed output hash, and the resulting workspace before it
/// advances the commit marker.
/// </summary>
public sealed class MaterializationRecoveryService
{
    public async Task<VerifiedLocalWorkspace> RecoverAsync(
        string outputRoot,
        string workspaceOutputPath,
        string? accountId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceOutputPath);
        cancellationToken.ThrowIfCancellationRequested();

        var fullRoot = Path.GetFullPath(outputRoot);
        var fullWorkspacePath = Path.GetFullPath(workspaceOutputPath);
        PathOverlapGuard.EnsureDisjoint(fullRoot, fullWorkspacePath);
        WorkspacePathSafety.EnsureNoReparsePoints(fullRoot);

        var state = await MaterializationStateStore.ReadAsync(fullRoot, cancellationToken).ConfigureAwait(false);
        if (state.State is MaterializationCommitStates.Staging)
        {
            throw new InvalidDataException("A staging-only materialization cannot be adopted.");
        }

        try
        {
            var manifestPath = Path.Combine(fullRoot, ".wechatvoice", "materialization-manifest.json");
            var manifest = await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            ValidateManifest(manifest);
            var effectiveAccountId = ResolveAccountId(accountId, manifest.AccountId);
            await VerifyManifestOutputsAsync(fullRoot, manifest, cancellationToken).ConfigureAwait(false);

            VerifiedLocalWorkspace verified;
            if (File.Exists(fullWorkspacePath))
            {
                verified = await ReadAndVerifyWorkspaceAsync(fullWorkspacePath, fullRoot, manifest, effectiveAccountId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var result = new MaterializationResult(
                    manifest.WorkspaceId,
                    manifest.SourceSnapshotId,
                    manifest.BackendId,
                    manifest.BackendVersion,
                    manifest.BackendSha256,
                    fullRoot,
                    manifest.Databases,
                    manifest.Files,
                    manifestPath,
                    manifest.KeyExtractionProfileId,
                    manifest.ProcessVersion,
                    manifest.ProcessImageSha256,
                    manifest.WcdbModuleSha256,
                    manifest.AccountSidFingerprint);
                var localWorkspace = await new LocalWorkspaceCreator().CreateAsync(
                    new VerifiedMaterialization(result, DateTimeOffset.UtcNow),
                    effectiveAccountId,
                    sourceIdentity: null,
                    cancellationToken).ConfigureAwait(false);
                await AtomicFileWriter.WriteJsonAsync(fullWorkspacePath, localWorkspace, InfrastructureJson.Indented, cancellationToken).ConfigureAwait(false);
                verified = await ReadAndVerifyWorkspaceAsync(fullWorkspacePath, fullRoot, manifest, effectiveAccountId, cancellationToken).ConfigureAwait(false);
            }

            await MaterializationStateStore.WriteAsync(fullRoot, MaterializationCommitStates.WorkspaceCommitted, cancellationToken).ConfigureAwait(false);
            await MaterializationStateStore.WriteAsync(fullRoot, MaterializationCommitStates.Completed, cancellationToken).ConfigureAwait(false);
            return verified;
        }
        catch (Exception)
        {
            try
            {
                await MaterializationStateStore.WriteAsync(fullRoot, MaterializationCommitStates.FailedRecoverable, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception stateException) when (stateException is IOException or UnauthorizedAccessException or InvalidDataException)
            {
            }

            throw;
        }
    }

    private static async Task<MaterializationManifest> ReadManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException("The materialization manifest is missing.");
        }

        await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<MaterializationManifest>(stream, InfrastructureJson.Compact, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The materialization manifest is empty.");
    }

    private static void ValidateManifest(MaterializationManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.WorkspaceId)
            || string.IsNullOrWhiteSpace(manifest.SourceSnapshotId)
            || string.IsNullOrWhiteSpace(manifest.BackendId)
            || string.IsNullOrWhiteSpace(manifest.BackendVersion)
            || string.IsNullOrWhiteSpace(manifest.BackendSha256)
            || manifest.Databases is null
            || manifest.Files is null)
        {
            throw new InvalidDataException("The materialization manifest is incomplete.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var path = NormalizeRelative(file.OutputRelativePath);
            if (MaterializationStateStore.IsStatePath(path)
                || path.Equals(".wechatvoice/materialization-manifest.json", StringComparison.OrdinalIgnoreCase)
                || !paths.Add(path))
            {
                throw new InvalidDataException("The materialization manifest contains a duplicate or mutable metadata file.");
            }

            if (file.ByteLength < 0 || string.IsNullOrWhiteSpace(file.Sha256))
            {
                throw new InvalidDataException($"The materialization manifest contains invalid file metadata: '{file.OutputRelativePath}'.");
            }
        }
    }

    private static string? ResolveAccountId(string? requestedAccountId, string? manifestAccountId)
    {
        if (requestedAccountId is not null
            && manifestAccountId is not null
            && !string.Equals(requestedAccountId, manifestAccountId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The requested account does not match the materialization manifest.");
        }

        return requestedAccountId ?? manifestAccountId;
    }

    private static async Task VerifyManifestOutputsAsync(
        string outputRoot,
        MaterializationManifest manifest,
        CancellationToken cancellationToken)
    {
        var expected = manifest.Files
            .Select(static file => NormalizeRelative(file.OutputRelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = Directory.EnumerateFiles(outputRoot, "*", SearchOption.AllDirectories)
            .Where(path => !IsControlMetadataPath(outputRoot, path))
            .Select(path => NormalizeRelative(Path.GetRelativePath(outputRoot, path)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actual.SetEquals(expected))
        {
            throw new InvalidDataException("The materialization output file set no longer matches its manifest.");
        }

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = CombineUnderRoot(outputRoot, file.OutputRelativePath);
            var info = new FileInfo(path);
            var hash = await FileHashing.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
            if (info.Length != file.ByteLength || !string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"A materialization output file changed: '{file.OutputRelativePath}'.");
            }
        }

        var covered = expected;
        foreach (var database in manifest.Databases.Where(static item => item.Status is MaterializationDatabaseStatus.Materialized or MaterializationDatabaseStatus.CopiedAsPlaintext))
        {
            if (string.IsNullOrWhiteSpace(database.OutputRelativePath)
                || !covered.Contains(NormalizeRelative(database.OutputRelativePath)))
            {
                throw new InvalidDataException($"The materialization manifest does not cover database output '{database.OutputRelativePath}'.");
            }
        }
    }

    private static async Task<VerifiedLocalWorkspace> ReadAndVerifyWorkspaceAsync(
        string workspacePath,
        string outputRoot,
        MaterializationManifest manifest,
        string? accountId,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(workspacePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var workspace = await JsonSerializer.DeserializeAsync<LocalWorkspace>(stream, InfrastructureJson.Compact, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The adopted workspace JSON is empty.");
        if (!string.Equals(Path.GetFullPath(workspace.SourceRoot), outputRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The adopted workspace does not point to the materialization output root.");
        }

        var provenance = workspace.Provenance
            ?? throw new InvalidDataException("The adopted workspace has no materialization provenance.");
        if (!string.Equals(provenance.MaterializationId, manifest.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(provenance.SourceSnapshotId, manifest.SourceSnapshotId, StringComparison.Ordinal)
            || !string.Equals(provenance.BackendId, manifest.BackendId, StringComparison.Ordinal)
            || !string.Equals(provenance.BackendVersion, manifest.BackendVersion, StringComparison.Ordinal)
            || !string.Equals(provenance.BackendBundleSha256, manifest.BackendSha256, StringComparison.OrdinalIgnoreCase)
            || (accountId is not null && !string.Equals(workspace.DataSet.AccountId, accountId, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The adopted workspace provenance does not match the materialization manifest.");
        }

        return await new LocalWorkspaceVerifier().VerifyAsync(workspace, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsControlMetadataPath(string root, string path)
    {
        var relative = NormalizeRelative(Path.GetRelativePath(root, path));
        return MaterializationStateStore.IsStatePath(relative)
            || relative.Equals(".wechatvoice/materialization-manifest.json", StringComparison.OrdinalIgnoreCase);
    }

    private static string CombineUnderRoot(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"The materialization output path is not relative: '{relativePath}'.");
        }

        var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The materialization output path escapes its root: '{relativePath}'.");
        }

        return candidate;
    }

    private static string NormalizeRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            throw new InvalidDataException($"The materialization output path is not relative: '{path}'.");
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.Equals(".", StringComparison.Ordinal)
            || normalized.StartsWith("../", StringComparison.Ordinal)
            || normalized.Contains("/../", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The materialization output path escapes its root: '{path}'.");
        }

        return normalized;
    }
}
