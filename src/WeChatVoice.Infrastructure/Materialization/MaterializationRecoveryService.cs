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
    public async Task<MaterializationRecoveryAssessment> AssessAsync(
        string outputRoot,
        string? workspaceOutputPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        cancellationToken.ThrowIfCancellationRequested();
        var fullRoot = Path.GetFullPath(outputRoot);
        var workspacePath = Path.GetFullPath(workspaceOutputPath ?? Path.Combine(
            Path.GetDirectoryName(fullRoot) ?? fullRoot,
            Path.GetFileName(fullRoot) + ".workspace.json"));
        if (!Directory.Exists(fullRoot))
        {
            return new MaterializationRecoveryAssessment(fullRoot, null, false, File.Exists(workspacePath));
        }

        MaterializationStateDocument state;
        try
        {
            state = await MaterializationStateStore.ReadAsync(fullRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new MaterializationRecoveryAssessment(fullRoot, null, false, File.Exists(workspacePath));
        }

        var eligibleState = state.State is MaterializationCommitStates.DatabasesCommitted or MaterializationCommitStates.FailedRecoverable;
        if (!eligibleState)
        {
            return new MaterializationRecoveryAssessment(fullRoot, state.State, false, File.Exists(workspacePath));
        }

        try
        {
            await ReadAndVerifyManifestAsync(fullRoot, cancellationToken).ConfigureAwait(false);
            return new MaterializationRecoveryAssessment(fullRoot, state.State, true, File.Exists(workspacePath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new MaterializationRecoveryAssessment(fullRoot, state.State, false, File.Exists(workspacePath));
        }
    }

    public async Task<VerifiedLocalWorkspace> RecoverAsync(
        string outputRoot,
        string workspaceOutputPath,
        string? accountId,
        CancellationToken cancellationToken,
        AccountIdentity? accountIdentity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceOutputPath);
        cancellationToken.ThrowIfCancellationRequested();

        var fullRoot = Path.GetFullPath(outputRoot);
        var fullWorkspacePath = Path.GetFullPath(workspaceOutputPath);
        PathOverlapGuard.EnsureDisjoint(fullRoot, fullWorkspacePath);
        WorkspacePathSafety.EnsureNoReparsePoints(fullRoot);

        MaterializationStateLock? stateLock = null;
        MaterializationStateDocument? state = null;
        var operationId = Guid.NewGuid().ToString("N");
        try
        {
            stateLock = await MaterializationStateStore.AcquireLockAsync(fullRoot, cancellationToken).ConfigureAwait(false);
            state = await MaterializationStateStore.ReadAsync(fullRoot, cancellationToken).ConfigureAwait(false);
            if (state.State is MaterializationCommitStates.Staging)
            {
                throw new InvalidDataException("A staging-only materialization cannot be adopted.");
            }

            var manifest = await ReadAndVerifyManifestAsync(fullRoot, cancellationToken).ConfigureAwait(false);
            var effectiveAccountId = ResolveAccountId(accountId ?? accountIdentity?.ConfirmedAccountId, manifest.AccountId);

            VerifiedLocalWorkspace verified;
            if (File.Exists(fullWorkspacePath))
            {
                verified = await ReadAndVerifyWorkspaceAsync(fullWorkspacePath, fullRoot, manifest, effectiveAccountId, cancellationToken).ConfigureAwait(false);
                if (accountIdentity is not null && verified.Workspace.AccountIdentity != accountIdentity)
                {
                    await LocalWorkspaceDocumentStore.WriteAsync(
                        fullWorkspacePath,
                        verified.Workspace.WithAccountIdentity(accountIdentity),
                        cancellationToken).ConfigureAwait(false);
                    verified = await ReadAndVerifyWorkspaceAsync(fullWorkspacePath, fullRoot, manifest, effectiveAccountId, cancellationToken).ConfigureAwait(false);
                }
                EnsureExistingWorkspaceIdentity(verified.Workspace.AccountIdentity, manifest, effectiveAccountId);
            }
            else if (state.State is MaterializationCommitStates.Completed or MaterializationCommitStates.WorkspaceCommitted)
            {
                throw new InvalidDataException("The committed local workspace document is missing.");
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
                    Path.Combine(fullRoot, ".wechatvoice", "materialization-manifest.json"),
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
                localWorkspace = localWorkspace.WithAccountIdentity(
                    accountIdentity
                    ?? (manifest.AccountEvidenceState == AccountEvidenceState.DatabaseConfirmed
                        ? new AccountIdentity(AccountIdentityState.Confirmed, null, UserConfirmationState.NotConfirmed, effectiveAccountId)
                        : AccountIdentity.CandidateOnly));
                EnsureRecoveryIdentity(localWorkspace.AccountIdentity, manifest, effectiveAccountId);
                await LocalWorkspaceDocumentStore.WriteAsync(fullWorkspacePath, localWorkspace, cancellationToken).ConfigureAwait(false);
                verified = await ReadAndVerifyWorkspaceAsync(fullWorkspacePath, fullRoot, manifest, effectiveAccountId, cancellationToken).ConfigureAwait(false);
            }

            if (state.State is MaterializationCommitStates.DatabasesCommitted or MaterializationCommitStates.FailedRecoverable)
            {
                await MaterializationStateStore.TransitionAsync(
                    fullRoot,
                    [MaterializationCommitStates.DatabasesCommitted, MaterializationCommitStates.FailedRecoverable],
                    MaterializationCommitStates.WorkspaceCommitted,
                    operationId,
                    failureCode: null,
                    cancellationToken: cancellationToken,
                    heldLock: stateLock!).ConfigureAwait(false);
                await MaterializationStateStore.TransitionAsync(
                    fullRoot,
                    [MaterializationCommitStates.WorkspaceCommitted],
                    MaterializationCommitStates.Completed,
                    operationId,
                    failureCode: null,
                    cancellationToken: cancellationToken,
                    heldLock: stateLock!).ConfigureAwait(false);
            }
            else if (state.State is MaterializationCommitStates.WorkspaceCommitted)
            {
                await MaterializationStateStore.TransitionAsync(
                    fullRoot,
                    [MaterializationCommitStates.WorkspaceCommitted],
                    MaterializationCommitStates.Completed,
                    operationId,
                    failureCode: null,
                    cancellationToken: cancellationToken,
                    heldLock: stateLock!).ConfigureAwait(false);
            }

            return verified;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is not evidence that committed output is damaged.
            // Leave the last durable state untouched so a later recovery can
            // resume or a user can inspect it safely.
            throw;
        }
        catch (Exception)
        {
            try
            {
                if (stateLock is not null)
                {
                    await MaterializationStateStore.TryTransitionToFailedRecoverableAsync(
                        fullRoot,
                        operationId,
                        "materialization-recovery-failed",
                        CancellationToken.None,
                        stateLock).ConfigureAwait(false);
                }
                else
                {
                    await MaterializationStateStore.TryTransitionToFailedRecoverableAsync(
                        fullRoot,
                        operationId: null,
                        failureCode: "materialization-recovery-failed",
                        cancellationToken: CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception stateException) when (stateException is IOException or UnauthorizedAccessException or InvalidDataException)
            {
            }

            throw;
        }
        finally
        {
            if (stateLock is not null)
            {
                await stateLock.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public static async Task<MaterializationManifest> ReadAndVerifyManifestAsync(
        string outputRoot,
        CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(outputRoot);
        WorkspacePathSafety.EnsureNoReparsePoints(fullRoot);
        var manifestPath = Path.Combine(fullRoot, ".wechatvoice", "materialization-manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException("The materialization manifest is missing.");
        }

        await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var manifest = await JsonSerializer.DeserializeAsync<MaterializationManifest>(stream, InfrastructureJson.Compact, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The materialization manifest is empty.");
        ValidateManifest(manifest);
        await VerifyManifestOutputsAsync(fullRoot, manifest, cancellationToken).ConfigureAwait(false);
        return manifest;
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
                || MaterializationStateStore.IsLockPath(path)
                || MaterializationStateStore.IsDurationCachePath(path)
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

    private static void EnsureRecoveryIdentity(
        AccountIdentity identity,
        MaterializationManifest manifest,
        string? effectiveAccountId)
    {
        if (effectiveAccountId is null)
        {
            return;
        }

        if (manifest.AccountEvidenceState == AccountEvidenceState.DatabaseConfirmed)
        {
            if (identity.AccountEvidenceState != AccountEvidenceState.DatabaseConfirmed
                || !string.Equals(identity.ConfirmedAccountId, effectiveAccountId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The recovered workspace does not preserve the verified database account evidence.");
            }

            return;
        }

        if (manifest.UserConfirmationState == UserConfirmationState.Confirmed
            && identity.UserConfirmation != UserConfirmationState.Confirmed)
        {
            throw new InvalidDataException("The recovered workspace lost the manifest's account confirmation.");
        }

        if (identity.UserConfirmation != UserConfirmationState.Confirmed
            || !string.Equals(identity.ConfirmedAccountId, effectiveAccountId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Account confirmation is required before a recovered workspace can be used.");
        }
    }

    private static void EnsureExistingWorkspaceIdentity(
        AccountIdentity identity,
        MaterializationManifest manifest,
        string? effectiveAccountId)
    {
        if (effectiveAccountId is null)
        {
            return;
        }

        if (manifest.AccountEvidenceState == AccountEvidenceState.DatabaseConfirmed)
        {
            if (identity.AccountEvidenceState != AccountEvidenceState.DatabaseConfirmed)
            {
                throw new InvalidDataException("The workspace does not preserve the manifest's database account evidence.");
            }

            return;
        }

        if (manifest.UserConfirmationState == UserConfirmationState.Confirmed
            && identity.UserConfirmation != UserConfirmationState.Confirmed)
        {
            throw new InvalidDataException("The workspace lost the manifest's account confirmation.");
        }

        if (identity.UserConfirmation != UserConfirmationState.Confirmed)
        {
            throw new InvalidDataException("The existing workspace has no recorded account confirmation.");
        }
    }

    public static async Task VerifyManifestOutputsAsync(
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

        if (manifest.ConfirmedAccountId is not null
            && (!string.Equals(workspace.AccountIdentity.ConfirmedAccountId, manifest.ConfirmedAccountId, StringComparison.Ordinal)
                || workspace.AccountIdentity.UserConfirmation != UserConfirmationState.Confirmed))
        {
            throw new InvalidDataException("The adopted workspace account confirmation does not match the materialization manifest.");
        }

        if (manifest.UserConfirmationState == UserConfirmationState.Confirmed
            && workspace.AccountIdentity.UserConfirmation != UserConfirmationState.Confirmed)
        {
            throw new InvalidDataException("The adopted workspace does not preserve the manifest's account confirmation.");
        }

        return await new LocalWorkspaceVerifier().VerifyAsync(workspace, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsControlMetadataPath(string root, string path)
    {
        var relative = NormalizeRelative(Path.GetRelativePath(root, path));
        return MaterializationStateStore.IsStatePath(relative)
            || MaterializationStateStore.IsLockPath(relative)
            || MaterializationStateStore.IsDurationCachePath(relative)
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
