using WeChatVoice.Core.Models;
using WeChatVoice.KeyAcquisition.Models;

namespace WeChatVoice.KeyAcquisition.Ports;

public interface IKeyAcquisitionService
{
    Task<VerifiedKeyAcquisition> AcquireAsync(
        VerifiedRawSnapshot snapshot,
        KeyAcquisitionOptions options,
        CancellationToken cancellationToken);
}

public interface IWeixinKeyExtractionProfile
{
    string Id { get; }

    WeixinKeyExtractionProfileDescriptor Descriptor { get; }

    Task<IReadOnlyList<ValidatedDatabaseKey>> AcquireAsync(
        VerifiedWeixinProcess process,
        VerifiedRawSnapshot snapshot,
        KeyAcquisitionBudget budget,
        CancellationToken cancellationToken);
}

/// <summary>
/// A profile that can inspect the fixed, identity-verified Weixin process tree
/// under one shared acquisition budget. Modern Weixin builds split sensitive
/// state between the UI root and same-image utility children; the broker still
/// supplies only processes that passed the exact profile identity policy.
/// </summary>
public interface IWeixinProcessTreeKeyExtractionProfile : IWeixinKeyExtractionProfile
{
    Task<IReadOnlyList<ValidatedDatabaseKey>> AcquireAsync(
        IReadOnlyList<VerifiedWeixinProcess> processes,
        VerifiedRawSnapshot snapshot,
        KeyAcquisitionBudget budget,
        CancellationToken cancellationToken);
}

public enum ProfileMaturity
{
    SyntheticOnly,
    ExperimentalLive,
    LiveValidated,
    Certified,
}

public sealed record WeixinKeyExtractionProfileDescriptor(
    IReadOnlySet<string> ProductVersions,
    IReadOnlySet<string> ImageSha256,
    string DatabaseEncryptionProfileId,
    string Architecture,
    ProfileMaturity Maturity = ProfileMaturity.SyntheticOnly);

/// <summary>
/// Extensible registry with exact evidence matching. Adding a reviewed Profile
/// does not widen the broker protocol or memory privileges.
/// </summary>
public sealed class WeixinKeyExtractionProfileRegistry(IEnumerable<IWeixinKeyExtractionProfile> profiles)
{
    private readonly IReadOnlyList<IWeixinKeyExtractionProfile> profiles =
        (profiles ?? throw new ArgumentNullException(nameof(profiles))).ToArray();

    public IReadOnlyList<IWeixinKeyExtractionProfile> Registered => profiles;

    public IWeixinKeyExtractionProfile? Match(VerifiedWeixinProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var matches = profiles.Where(profile =>
            profile.Descriptor.ProductVersions.Contains(process.ProductVersion) &&
            profile.Descriptor.ImageSha256.Contains(process.ImageSha256) &&
            string.Equals(profile.Descriptor.Architecture, process.Architecture, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException("More than one key-extraction Profile matched the verified process identity."),
        };
    }
}

/// <summary>
/// Only non-sensitive process identity is allowed to cross the profile
/// boundary. A profile implementation must perform its own open-handle
/// revalidation before any future memory read is added.
/// </summary>
public sealed record VerifiedWeixinProcess(
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    string ImagePath,
    string ImageSha256,
    string ProductVersion,
    string OwnerSid,
    int SessionId,
    string Architecture);
