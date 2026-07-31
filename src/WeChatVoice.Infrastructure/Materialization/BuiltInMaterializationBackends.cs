using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;

namespace WeChatVoice.Infrastructure.Materialization;

/// <summary>
/// Single registry for formal materialization backends. The Weixin backend is
/// intentionally registered as unavailable until a verified version-specific
/// key/decryption profile is supplied; registration alone is not usability.
/// </summary>
public static class BuiltInMaterializationBackends
{
    public static IReadOnlyList<IDatabaseMaterializationBackend> Create()
        => [new WeixinWindows4MaterializationBackend()];
}

public sealed class WeixinWindows4MaterializationBackend : IDatabaseMaterializationBackend
{
    public string Id => "weixin-windows-4";

    public string Version => "profile-unavailable";

    public string ExpectedBinarySha256 => "not-installed";

    public Task<VerifiedMaterialization> MaterializeAsync(
        VerifiedRawSnapshot snapshot,
        MaterializationOptions options,
        CancellationToken cancellationToken)
        => throw new MaterializationBackendUnavailableException(Id, "No verified Weixin Windows 4.x key and database-encryption profile is installed.");
}

public sealed class MaterializationBackendUnavailableException : InvalidOperationException
{
    public MaterializationBackendUnavailableException(string backendId, string message)
        : base(message)
    {
        BackendId = backendId;
    }

    public string BackendId { get; }
}
