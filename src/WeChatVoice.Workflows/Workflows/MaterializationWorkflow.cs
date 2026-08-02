using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Materialization;
using WeChatVoice.Infrastructure.Sqlite;

namespace WeChatVoice.Workflows.Workflows;

/// <summary>
/// Runs the fixed materialization chain: verify the raw snapshot, derive and
/// confirm the account candidate, execute the chosen materialization backend
/// (one-shot elevated Key Broker by default, development-only external
/// decryptor when explicitly allowed), then verify the produced workspace and
/// cross-check that its account is the confirmed one. Hosts never compose
/// these steps themselves and never touch a Broker implementation.
/// </summary>
public sealed class MaterializationWorkflow : IMaterializationWorkflow
{
    private readonly IMaterializationExecutor _brokerExecutor;
    private readonly Func<string, IMaterializationExecutor> _externalExecutorFactory;
    private readonly Workspaces.WorkspaceLoader _loader;
    private readonly MaterializationOptionsFactory _optionsFactory;

    public MaterializationWorkflow(
        IMaterializationExecutor brokerExecutor,
        Func<string, IMaterializationExecutor>? externalExecutorFactory = null,
        Workspaces.WorkspaceLoader? loader = null,
        MaterializationOptionsFactory? optionsFactory = null)
    {
        _brokerExecutor = brokerExecutor ?? throw new ArgumentNullException(nameof(brokerExecutor));
        _externalExecutorFactory = externalExecutorFactory ?? (path => new ExternalMaterializationExecutor(
            new ExternalDatabaseMaterializer(path),
            new LocalWorkspaceCreator()));
        _loader = loader ?? new Workspaces.WorkspaceLoader();
        _optionsFactory = optionsFactory ?? new MaterializationOptionsFactory();
    }

    /// <summary>
    /// Validates the request and selects the materialization backend: the
    /// formal broker executor by default, or the development-only external
    /// decryptor when the caller explicitly opted into an untrusted backend.
    /// </summary>
    internal IMaterializationExecutor SelectExecutor(MaterializationWorkflowRequest request)
    {
        if (!string.Equals(request.BackendId, "weixin-windows-4", StringComparison.OrdinalIgnoreCase))
        {
            throw new MaterializationBackendUnavailableException(request.BackendId, $"Materialization backend '{request.BackendId}' is not registered.");
        }

        if (request.ExternalDecryptorPath is not null)
        {
            if (!request.AllowUntrustedBackend)
            {
                throw new ArgumentException("--external-decryptor is development-only and requires --allow-untrusted-backend.");
            }

            return _externalExecutorFactory(request.ExternalDecryptorPath);
        }

        if (request.AllowUntrustedBackend)
        {
            throw new ArgumentException("--allow-untrusted-backend requires --external-decryptor.");
        }

        return _brokerExecutor;
    }

    public async Task<MaterializationWorkflowResult> RunAsync(
        MaterializationWorkflowRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!context.TryStart())
        {
            throw new InvalidOperationException("The workflow state machine is not idle.");
        }

        try
        {
            // Validate the request and select the backend before any file
            // access so parameter errors surface before snapshot I/O.
            var executor = SelectExecutor(request);
            context.Report(OperationPhase.Materialization, OperationStageIds.VerifyingSnapshot);
            var snapshotRoot = Path.GetFullPath(request.SnapshotDirectory);
            var manifestPath = Path.GetFullPath(request.SnapshotManifestPath ?? Path.Combine(snapshotRoot, ".wechatvoice", "snapshot-manifest.json"));
            var manifest = await _optionsFactory.ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            var verifiedSnapshot = await new RawSnapshotVerifier()
                .VerifyAsync(new RawSnapshot(manifest, snapshotRoot), cancellationToken)
                .ConfigureAwait(false);

            // Account identity is path-derived and only a candidate. The user
            // must explicitly confirm the detected account before a privileged
            // materialization runs; nothing is silent.
            context.Report(OperationPhase.Materialization, OperationStageIds.ConfirmingAccount);
            var sourceIdentity = SnapshotSourceIdentity.TryDerive(manifest.SourceDirectory, manifest.Files);
            var confirmedAccountId = await ConfirmAccountAsync(
                sourceIdentity?.AccountCandidate,
                request.RequestedAccountId,
                context,
                cancellationToken).ConfigureAwait(false);

            var outputRoot = Path.GetFullPath(request.OutputDirectory);
            var localWorkspacePath = Path.GetFullPath(request.WorkspaceOutputPath ?? Path.Combine(
                Path.GetDirectoryName(outputRoot) ?? throw new InvalidDataException("The materialization output must have a parent directory."),
                Path.GetFileName(outputRoot) + ".workspace.json"));
            PathOverlapGuard.EnsureDisjoint(snapshotRoot, outputRoot, localWorkspacePath);

            var executed = await executor.ExecuteAsync(
                verifiedSnapshot,
                manifestPath,
                outputRoot,
                localWorkspacePath,
                confirmedAccountId,
                cancellationToken,
                context.Progress).ConfigureAwait(false);

            context.Report(OperationPhase.Materialization, OperationStageIds.VerifyingWorkspace);
            var verifiedWorkspace = await _loader.LoadVerifiedAsync(executed.LocalWorkspacePath, cancellationToken).ConfigureAwait(false);
            if (confirmedAccountId is not null
                && !string.Equals(verifiedWorkspace.DataSet.AccountId, confirmedAccountId, StringComparison.Ordinal))
            {
                throw new AppFailureException(ErrorCode.WorkspaceInvalid, "The materialization produced a workspace for a different account than the one confirmed.");
            }

            // The backend can only establish the path/database candidate at
            // this boundary. Persist the user's decision separately without
            // upgrading technical evidence to Confirmed. The document is
            // atomically rewritten and verified again before it crosses back
            // to the host.
            var identity = confirmedAccountId is not null
                ? new AccountIdentity(AccountIdentityState.Candidate, null, UserConfirmationState.Confirmed)
                : AccountIdentity.CandidateOnly;
            var persistedWorkspace = verifiedWorkspace.Workspace.WithAccountIdentity(identity);
            if (verifiedWorkspace.Workspace.AccountIdentity != identity)
            {
                await LocalWorkspaceDocumentStore.WriteAsync(
                    executed.LocalWorkspacePath,
                    persistedWorkspace,
                    cancellationToken).ConfigureAwait(false);
                verifiedWorkspace = await _loader.LoadVerifiedAsync(executed.LocalWorkspacePath, cancellationToken).ConfigureAwait(false);
            }
            context.StateMachine.TryComplete();
            context.Report(OperationPhase.Materialization, OperationStageIds.Completing);
            return new MaterializationWorkflowResult(
                verifiedWorkspace,
                executed.LocalWorkspacePath,
                identity,
                executed.ProfileId,
                executed.MaterializationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            context.StateMachine.TryCancel();
            context.Report(OperationPhase.Materialization, OperationStageIds.Starting, "物料化已取消");
            throw;
        }
        catch
        {
            if (Directory.Exists(request.OutputDirectory))
            {
                try
                {
                    await MaterializationStateStore.TryTransitionToFailedRecoverableAsync(
                        request.OutputDirectory,
                        operationId: null,
                        failureCode: ErrorCode.MaterializationInvalid.ToString(),
                        cancellationToken: CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception stateException) when (stateException is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                }
            }

            context.StateMachine.TryFail();
            throw;
        }
    }

    /// <summary>
    /// Resolves and confirms the path-derived account candidate before a
    /// privileged materialization runs. An explicit requested account that
    /// matches the candidate counts as confirmation; otherwise the
    /// host-supplied confirmation port prompts the user. A null candidate
    /// (unrecognized layout) proceeds and workspace validation later refuses it.
    /// </summary>
    internal static async Task<string?> ConfirmAccountAsync(
        string? candidate,
        string? requestedAccount,
        WorkflowContext context,
        CancellationToken cancellationToken)
    {
        if (candidate is null)
        {
            return null;
        }

        if (requestedAccount is not null)
        {
            if (!string.Equals(requestedAccount, candidate, StringComparison.Ordinal))
            {
                throw new AppFailureException(ErrorCode.AccountConfirmationRequired, $"The requested account '{requestedAccount}' does not match the detected account '{candidate}'.");
            }

            return candidate;
        }

        context.StateMachine.TryEnterAwaitingUser();
        context.Report(OperationPhase.Materialization, OperationStageIds.ConfirmingAccount, "等待账号确认");
        try
        {
            var confirmation = await context.AccountConfirmation.ConfirmAsync(
                new AccountIdentityReport(candidate, AccountIdentityState.Candidate, null),
                cancellationToken).ConfigureAwait(false);
            if (!confirmation.Confirmed || !string.Equals(confirmation.ConfirmedAccountId, candidate, StringComparison.Ordinal))
            {
                throw new AppFailureException(ErrorCode.AccountConfirmationRequired, "Account confirmation was declined.");
            }

            return candidate;
        }
        finally
        {
            context.StateMachine.TryResumeFromUser();
        }
    }
}

public sealed record MaterializationWorkflowRequest(
    string SnapshotDirectory,
    string? SnapshotManifestPath,
    string BackendId,
    string? ExternalDecryptorPath,
    bool AllowUntrustedBackend,
    string? RequestedAccountId,
    string OutputDirectory,
    string? WorkspaceOutputPath);

public sealed record MaterializationWorkflowResult(
    VerifiedLocalWorkspace Workspace,
    string LocalWorkspacePath,
    AccountIdentity AccountIdentity,
    string? ProfileId,
    string? MaterializationId);

/// <summary>
/// Seam between the workflow and a materialization backend. Hosts and tests
/// inject a fake executor; the built-in executors wrap the Key Broker client
/// and the development-only external decryptor.
/// </summary>
public interface IMaterializationExecutor
{
    string Id { get; }

    Task<ExecutedMaterialization> ExecuteAsync(
        VerifiedRawSnapshot snapshot,
        string snapshotManifestPath,
        string outputRoot,
        string localWorkspacePath,
        string? confirmedAccountId,
        CancellationToken cancellationToken,
        IProgress<OperationProgress>? progress = null);
}

public sealed record ExecutedMaterialization(
    string LocalWorkspacePath,
    string? ProfileId,
    string? MaterializationId);

/// <summary>Small options factory kept separate so tests can stub JSON reads.</summary>
public sealed class MaterializationOptionsFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public async Task<SnapshotManifest> ReadManifestAsync(string manifestPath, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(manifestPath);
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<SnapshotManifest>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"The JSON document was empty: '{fullPath}'.");
    }
}
