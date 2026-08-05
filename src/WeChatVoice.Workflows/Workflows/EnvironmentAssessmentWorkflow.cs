using System.ComponentModel;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Adapters;
using WeChatVoice.KeyProfileMetadata;
using WeChatVoice.Windows;
using WeChatVoice.Workflows.Broker;
using KeyProfileMetadataModel = WeChatVoice.KeyProfileMetadata.KeyProfileMetadata;

namespace WeChatVoice.Workflows.Workflows;

/// <summary>
/// Reports installed capabilities: running Weixin processes, matching key
/// acquisition profiles, registered adapters, and whether the fixed worker and
/// Broker binaries are present next to the host. Optionally verifies a local
/// workspace and probes it against registered adapters. The supported build is
/// the live-validated Weixin 4.1.11.55; other versions are reported as
/// unsupported and no workflow proceeds.
/// </summary>
public sealed class EnvironmentAssessmentWorkflow : IEnvironmentAssessmentWorkflow
{
    private readonly Workspaces.WorkspaceLoader _loader;
    private readonly IReadOnlyList<KeyProfileMetadataModel> _keyProfiles;
    private readonly IReadOnlyList<string> _registeredAdapters;
    private readonly IBrokerTrustPolicy _brokerTrustPolicy;

    public EnvironmentAssessmentWorkflow(
        Workspaces.WorkspaceLoader? loader = null,
        IReadOnlyList<KeyProfileMetadataModel>? keyProfiles = null,
        IReadOnlyList<string>? registeredAdapters = null,
        IBrokerTrustPolicy? brokerTrustPolicy = null)
    {
        _loader = loader ?? new Workspaces.WorkspaceLoader();
        _keyProfiles = keyProfiles ?? BuiltInKeyProfileMetadata.Create();
        _registeredAdapters = registeredAdapters
            ?? BuiltInAdapters.Create().Select(static adapter => adapter.Id).Order(StringComparer.Ordinal).ToArray();
        _brokerTrustPolicy = brokerTrustPolicy ?? new ReleaseBrokerTrustPolicy();
    }

    public async Task<EnvironmentAssessmentResult> RunAsync(
        EnvironmentAssessmentRequest request,
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
            context.Report(OperationPhase.EnvironmentAssessment, OperationStageIds.DetectingWeixin);
            var runningProcesses = WeChatProcessDiscovery.ListRunning();
            var matchingProfiles = GetMatchingKeyProfiles(_keyProfiles);

            var matchingAdapters = Array.Empty<string>();
            VerifiedLocalWorkspace? workspace = null;
            if (!string.IsNullOrWhiteSpace(request.WorkspacePath))
            {
                context.Report(OperationPhase.EnvironmentAssessment, OperationStageIds.VerifyingWorkspace);
                workspace = await _loader.LoadVerifiedAsync(request.WorkspacePath, cancellationToken).ConfigureAwait(false);
                matchingAdapters = BuiltInAdapters.Create()
                    .Where(adapter => adapter.Probe(workspace.DataSet).IsMatch)
                    .Select(static adapter => adapter.Id)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
            }

            var workerInstalled = File.Exists(Path.Combine(AppContext.BaseDirectory, "WeChatVoice.SqlCipherWorker.exe"));
            var brokerInstalled = File.Exists(Path.Combine(AppContext.BaseDirectory, "WeChatVoice.KeyBroker.exe"));
            var brokerPath = Path.Combine(AppContext.BaseDirectory, "WeChatVoice.KeyBroker.exe");
            var brokerTrust = brokerInstalled
                ? _brokerTrustPolicy.Verify(brokerPath)
                : BrokerTrustResult.Deny("broker-not-installed");
            var workerTrust = workerInstalled
                ? await WorkerBundleTrustEvaluator.VerifyAsync(AppContext.BaseDirectory, cancellationToken).ConfigureAwait(false)
                : WorkerBundleTrustResult.Deny("worker-not-installed");
            var installSecurity = AssessInstallDirectorySecurity(_brokerTrustPolicy, brokerTrust);
            context.StateMachine.TryComplete();
            context.Report(OperationPhase.EnvironmentAssessment, OperationStageIds.Completing);
            return new EnvironmentAssessmentResult(
                IsWindows: OperatingSystem.IsWindows(),
                RunningWeChatProcesses: runningProcesses,
                SupportedProcessNames: WeChatProcessDiscovery.SupportedProcessNames,
                KeyAcquisitionProfiles: _keyProfiles,
                MatchingKeyAcquisitionProfiles: matchingProfiles,
                RegisteredAdapters: _registeredAdapters,
                AdapterMatchEvaluated: !string.IsNullOrWhiteSpace(request.WorkspacePath),
                MatchingAdapters: matchingAdapters,
                WorkerInstalled: workerInstalled,
                BrokerInstalled: brokerInstalled,
                BrokerAcquireAndMaterializeAvailable: brokerInstalled && workerInstalled && matchingProfiles.Count > 0 && brokerTrust.Verified && workerTrust.Verified,
                Workspace: workspace,
                BrokerTrustResult: brokerTrust,
                WorkerBundleTrustResult: workerTrust,
                InstallDirectorySecurity: installSecurity);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            context.StateMachine.TryCancel();
            throw;
        }
        catch
        {
            context.StateMachine.TryFail();
            throw;
        }
    }

    internal static InstallDirectorySecurityResult AssessInstallDirectorySecurity(
        IBrokerTrustPolicy trustPolicy,
        BrokerTrustResult brokerTrust)
    {
        ArgumentNullException.ThrowIfNull(trustPolicy);
        ArgumentNullException.ThrowIfNull(brokerTrust);

        if (trustPolicy is DevelopmentBrokerTrustPolicy)
        {
            return new InstallDirectorySecurityResult(
                Protected: false,
                UserWritable: false,
                NonSensitiveReason: null,
                Writeability: UserWriteability.Indeterminate,
                SecurityState: InstallSecurityState.DevelopmentModeNotApplicable);
        }

        if (brokerTrust.Verified)
        {
            return new InstallDirectorySecurityResult(
                Protected: true,
                UserWritable: false,
                NonSensitiveReason: null,
                Writeability: UserWriteability.NotWritable,
                SecurityState: InstallSecurityState.VerifiedProtected);
        }

        if (string.Equals(brokerTrust.NonSensitiveReason, "install-directory-user-writable", StringComparison.Ordinal))
        {
            return new InstallDirectorySecurityResult(
                Protected: false,
                UserWritable: true,
                NonSensitiveReason: brokerTrust.NonSensitiveReason,
                Writeability: UserWriteability.Writable,
                SecurityState: InstallSecurityState.UserWritable);
        }

        if (string.Equals(brokerTrust.NonSensitiveReason, "install-directory-writeability-indeterminate", StringComparison.Ordinal))
        {
            return new InstallDirectorySecurityResult(
                Protected: false,
                UserWritable: false,
                NonSensitiveReason: brokerTrust.NonSensitiveReason,
                Writeability: UserWriteability.Indeterminate,
                SecurityState: InstallSecurityState.Indeterminate);
        }

        // Publisher, signature, bundle, and installation-path failures occur
        // before ReleaseBrokerTrustPolicy probes writeability. Do not present
        // that as an ACL/filesystem failure to the user.
        return new InstallDirectorySecurityResult(
            Protected: false,
            UserWritable: false,
            NonSensitiveReason: brokerTrust.NonSensitiveReason,
            Writeability: UserWriteability.Indeterminate,
            SecurityState: InstallSecurityState.NotEvaluated);
    }

    /// <summary>
    /// Matches live Weixin processes against the registered key profiles using
    /// read-only identity evidence (owner SID, Authenticode signature, product
    /// version, image hash, architecture). Non-matching processes are skipped,
    /// never fatal.
    /// </summary>
    internal static IReadOnlyList<string> GetMatchingKeyProfiles(IReadOnlyList<KeyProfileMetadataModel> profiles)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var currentSid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(currentSid))
        {
            return [];
        }

        var reader = new WindowsWeixinProcessIdentityReader();
        var matches = new HashSet<string>(StringComparer.Ordinal);
        foreach (var process in new WeixinProcessLocator().Locate())
        {
            WeixinProcessIdentityEvidence evidence;
            try
            {
                evidence = reader.Read(process.ProcessId);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
            {
                continue;
            }

            foreach (var profile in profiles)
            {
                if (string.Equals(evidence.OwnerSid, currentSid, StringComparison.Ordinal)
                    && evidence.HasTrustedSignature
                    && evidence.SignerSubject.Contains("Tencent", StringComparison.OrdinalIgnoreCase)
                    && profile.ProductVersions.Contains(evidence.ProductVersion)
                    && profile.ImageSha256.Contains(evidence.ImageSha256, StringComparer.OrdinalIgnoreCase)
                    && string.Equals(profile.Architecture, evidence.Architecture, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(profile.Id);
                }
            }
        }

        return matches.Order(StringComparer.Ordinal).ToArray();
    }
}
