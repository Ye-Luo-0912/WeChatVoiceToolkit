using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using WeChatVoice.Core.Models;
using WeChatVoice.KeyAcquisition.Models;
using WeChatVoice.KeyAcquisition.Ports;
using WeChatVoice.Windows;

namespace WeChatVoice.KeyBroker;

/// <summary>
/// The broker's concrete acquisition composition. It discovers only the
/// signed Weixin process, re-verifies its identity immediately before opening
/// a read-only memory handle, and hands the resulting group-bound buffers to
/// the ephemeral acquisition contract.
/// </summary>
internal sealed class ProfileDrivenKeyAcquisitionService(
    WeixinProcessLocator processLocator,
    IWeixinProcessIdentityReader identityReader,
    IEnumerable<IWeixinKeyExtractionProfile> profiles,
    Action<BrokerStageEvent>? stageReporter = null) : IKeyAcquisitionService
{
    private readonly WeixinProcessLocator processLocator = processLocator ?? throw new ArgumentNullException(nameof(processLocator));
    private readonly IWeixinProcessIdentityReader identityReader = identityReader ?? throw new ArgumentNullException(nameof(identityReader));
    private readonly IReadOnlyList<IWeixinKeyExtractionProfile> profiles = (profiles ?? throw new ArgumentNullException(nameof(profiles))).ToArray();
    private readonly Action<BrokerStageEvent>? reportStage = stageReporter;

    public async Task<VerifiedKeyAcquisition> AcquireAsync(
        VerifiedRawSnapshot snapshot,
        KeyAcquisitionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ProfileId))
        {
            throw new ArgumentException("A key-extraction Profile is required.", nameof(options));
        }

        var budget = options.Budget;

        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The Weixin Key Broker is Windows-only.");
        }

        var currentSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new UnauthorizedAccessException("The current Windows user SID was unavailable.");
        reportStage?.Invoke(new BrokerStageEvent("process-locating"));
        var matches = new List<(IWeixinKeyExtractionProfile Profile, WeChatProcessInfo Process, WeixinProcessIdentityEvidence Evidence)>();
        foreach (var process in processLocator.LocateTrustedProcessTree())
        {
            cancellationToken.ThrowIfCancellationRequested();
            WeixinProcessIdentityEvidence evidence;
            try
            {
                evidence = identityReader.Read(process.ProcessId);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                continue;
            }

            foreach (var profile in profiles)
            {
                var descriptor = profile.Descriptor;
                if (!string.Equals(evidence.OwnerSid, currentSid, StringComparison.Ordinal)
                    || !evidence.HasTrustedSignature
                    || !evidence.SignerSubject.Contains("Tencent", StringComparison.OrdinalIgnoreCase)
                    || !descriptor.ProductVersions.Contains(evidence.ProductVersion)
                    || !descriptor.ImageSha256.Contains(evidence.ImageSha256, StringComparer.OrdinalIgnoreCase)
                    || !string.Equals(descriptor.Architecture, evidence.Architecture, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(profile.Id, options.ProfileId, StringComparison.Ordinal))
                {
                    continue;
                }

                matches.Add((profile, process, evidence));
            }
        }

        if (matches.Count == 0)
        {
            throw new InvalidDataException("No running Weixin process matched the requested exact key-extraction Profile.");
        }

        reportStage?.Invoke(new BrokerStageEvent("process-matched"));

        var profileGroups = matches.GroupBy(static match => match.Profile.Id, StringComparer.Ordinal).ToArray();
        if (profileGroups.Length != 1)
        {
            throw new InvalidDataException("More than one key-extraction Profile matched the verified Weixin process tree.");
        }

        var profileMatches = profileGroups[0].ToArray();
        var match = profileMatches[0];
        if (match.Profile.Descriptor.Maturity == ProfileMaturity.ExperimentalLive && !options.AllowExperimentalProfile)
        {
            throw new InvalidDataException("The selected key-extraction Profile is experimental; explicit opt-in is required.");
        }
        var verifier = new ProcessIdentityVerifier(identityReader);
        var verifiedProcesses = new List<VerifiedWeixinProcess>(profileMatches.Length);
        foreach (var processMatch in profileMatches)
        {
            var policy = new WeixinProcessIdentityPolicy(
                processMatch.Evidence.ProductVersion,
                processMatch.Evidence.ImageSha256,
                processMatch.Evidence.OwnerSid,
                processMatch.Evidence.SessionId,
                processMatch.Evidence.Architecture);
            verifiedProcesses.Add(verifier.Verify(processMatch.Process.ProcessId, policy));
        }

        reportStage?.Invoke(new BrokerStageEvent("process-verified"));
        var validated = match.Profile is IWeixinProcessTreeKeyExtractionProfile treeProfile
            ? await treeProfile.AcquireAsync(verifiedProcesses, snapshot, budget, cancellationToken).ConfigureAwait(false)
            : await match.Profile.AcquireAsync(verifiedProcesses[0], snapshot, budget, cancellationToken).ConfigureAwait(false);
        try
        {
            reportStage?.Invoke(new BrokerStageEvent(
                "keys-validated",
                CompletedGroups: validated.Count,
                TotalGroups: validated.Count));
            var bindings = new List<DatabaseKeyBinding>(validated.Count);
            foreach (var key in validated)
            {
                if (string.IsNullOrWhiteSpace(key.SourceRelativePath))
                {
                    throw new InvalidDataException("A validated key did not retain its verified database-group path.");
                }

                bindings.Add(new DatabaseKeyBinding(
                    snapshot.Snapshot.SnapshotId,
                    match.Evidence.OwnerSid,
                    key.DatabaseGroupFingerprint,
                    key.SourceRelativePath,
                    key.ShardNumber,
                    match.Profile.Id,
                    key.EncryptionProfileId
                        ?? throw new InvalidDataException("A validated key did not retain its exact database-encryption Profile."),
                    key.KeyMaterial));
            }

            var acquisition = new VerifiedKeyAcquisition(
                "acquisition-" + Guid.NewGuid().ToString("N"),
                snapshot.Snapshot.SnapshotId,
                match.Profile.Id,
                bindings,
                DateTimeOffset.UtcNow,
                match.Evidence.ProductVersion,
                match.Evidence.ImageSha256,
                match.Profile is WeixinWindows41155Profile ? WeixinWindows41155Profile.SupportedWcdbModuleSha256 : null,
                ComputeSidFingerprint(match.Evidence.OwnerSid));
            return acquisition;
        }
        catch
        {
            foreach (var key in validated)
            {
                key.KeyMaterial.Dispose();
            }

            throw;
        }
    }

    private static string ComputeSidFingerprint(string sid)
    {
        var bytes = Encoding.UTF8.GetBytes("WeChatVoiceToolkit:SID:v1:" + sid);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
