using System.Security.Principal;
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
        var matches = new List<(IWeixinKeyExtractionProfile Profile, WeChatProcessInfo Process, WeixinProcessIdentityEvidence Evidence)>();
        foreach (var process in processLocator.Locate())
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

        if (matches.Count != 1)
        {
            throw new InvalidDataException("More than one running Weixin process matched the requested Profile.");
        }

        var match = matches[0];
        if (match.Profile.Descriptor.Maturity == ProfileMaturity.ExperimentalLive && !options.AllowExperimentalProfile)
        {
            throw new InvalidDataException("The selected key-extraction Profile is experimental; explicit opt-in is required.");
        }
        var policy = new WeixinProcessIdentityPolicy(
            match.Evidence.ProductVersion,
            match.Evidence.ImageSha256,
            match.Evidence.OwnerSid,
            match.Evidence.SessionId,
            match.Evidence.Architecture);
        var verifiedProcess = new ProcessIdentityVerifier(identityReader).Verify(match.Process.ProcessId, policy);
        reportStage?.Invoke(new BrokerStageEvent("process-verified"));
        var validated = await match.Profile.AcquireAsync(verifiedProcess, snapshot, budget, cancellationToken).ConfigureAwait(false);
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
                    match.Profile.Descriptor.DatabaseEncryptionProfileId,
                    key.KeyMaterial));
            }

            var acquisition = new VerifiedKeyAcquisition(
                "acquisition-" + Guid.NewGuid().ToString("N"),
                snapshot.Snapshot.SnapshotId,
                match.Profile.Id,
                bindings,
                DateTimeOffset.UtcNow);
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
}
