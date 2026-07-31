using WeChatVoice.KeyAcquisition.Ports;

namespace WeChatVoice.KeyAcquisition.Validation;

/// <summary>
/// Exact validation set observed at the Weixin 4.1.11.55 WCDB call sites.
/// It does not guess arbitrary SQLCipher settings: only compatibility 3 with
/// the explicit 4096-byte override and compatibility 4 are accepted.
/// </summary>
public sealed class WeixinWindows41155SqlCipherKeyValidator : IDatabaseKeyValidator
{
    private readonly WeixinWindows4SqlCipher3Page4096KeyValidator version3 = new();
    private readonly WeixinWindows4SqlCipherKeyValidator version4 = new();

    public string Id => "weixin-windows-4.1.11.55.sqlcipher-exact-set-v1";

    public DatabaseKeyValidationResult ValidateFirstPage(
        ReadOnlySpan<byte> encryptedFirstPage,
        ReadOnlySpan<byte> candidateKey)
    {
        var version3Result = version3.ValidateFirstPage(encryptedFirstPage, candidateKey);
        if (version3Result.IsValid)
        {
            return version3Result;
        }

        return version3Result.Failure is DatabaseKeyValidationFailure.InvalidPageLength or DatabaseKeyValidationFailure.InvalidKeyLength
            ? version3Result
            : version4.ValidateFirstPage(encryptedFirstPage, candidateKey);
    }
}
