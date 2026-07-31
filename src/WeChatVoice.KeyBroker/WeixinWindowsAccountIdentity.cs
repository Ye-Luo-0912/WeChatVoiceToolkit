namespace WeChatVoice.KeyBroker;

/// <summary>
/// Extracts only the account candidate encoded by the fixed Weixin storage
/// layout. The Adapter must still verify this candidate against decrypted
/// contact and message Name2Id tables before opening a Catalog.
/// </summary>
internal static class WeixinWindowsAccountIdentity
{
    internal static string? TryDeriveCandidate(string sourceDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            return null;
        }

        var storage = new DirectoryInfo(Path.GetFullPath(sourceDirectory));
        if (!string.Equals(storage.Name, "db_storage", StringComparison.OrdinalIgnoreCase)
            || storage.Parent is not { } accountDirectory)
        {
            return null;
        }

        var name = accountDirectory.Name;
        var separator = name.LastIndexOf('_');
        if (separator <= "wxid_".Length
            || separator + 5 != name.Length
            || !name.StartsWith("wxid_", StringComparison.Ordinal)
            || !name.AsSpan(separator + 1).ContainsOnlyHexDigits())
        {
            return null;
        }

        return name[..separator];
    }

    private static bool ContainsOnlyHexDigits(this ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
