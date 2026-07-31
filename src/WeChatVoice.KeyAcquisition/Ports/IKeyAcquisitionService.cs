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

    Task<IReadOnlyList<ValidatedDatabaseKey>> AcquireAsync(
        VerifiedWeixinProcess process,
        VerifiedRawSnapshot snapshot,
        CancellationToken cancellationToken);
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
