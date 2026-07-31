namespace WeChatVoice.KeyProfileMetadata;

/// <summary>
/// Non-sensitive metadata exposed to ordinary processes. It deliberately has
/// no scanner, memory reader, key validator, or key-material API.
/// </summary>
public sealed record KeyProfileMetadata(
    string Id,
    IReadOnlySet<string> ProductVersions,
    IReadOnlySet<string> ImageSha256,
    string DatabaseEncryptionProfileId,
    string Architecture,
    string Maturity);

public static class BuiltInKeyProfileMetadata
{
    public static IReadOnlyList<KeyProfileMetadata> Create() =>
    [
        new KeyProfileMetadata(
            "weixin-windows-4.1.11.55-wcdb-ascii-key-v1",
            new HashSet<string>(StringComparer.Ordinal) { "4.1.11.55" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ac599744a7ce7b65640ebe18c939c0d4e4a06cd039d89cddee7f1e9afc56875d" },
            "weixin-windows-4.sqlcipher4-page-hmac-sha512-v1",
            "x64",
            "experimental-live"),
    ];
}
