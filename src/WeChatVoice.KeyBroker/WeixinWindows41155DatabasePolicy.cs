namespace WeChatVoice.KeyBroker;

/// <summary>
/// Exact source coverage policy for the observed 4.1.11.55 data layout. The
/// Only the message/media/contact databases are required by the voice export
/// chain. Search indexes, thumbnails, sessions, migration stores, and other
/// auxiliary databases are still audited in the materialization manifest but
/// may be recorded as intentionally ignored. This prevents an optional FTS
/// extension from blocking the voice path on Windows SQLite builds that do not
/// carry the matching extension.
/// </summary>
internal static class WeixinWindows41155DatabasePolicy
{
    internal static bool CanIntentionallyIgnore(string relativePath) =>
        !IsRequiredVoiceDatabase(relativePath);

    private static bool IsRequiredVoiceDatabase(string relativePath)
    {
        var normalized = Normalize(relativePath);
        var fileName = Path.GetFileName(normalized);
        if (fileName.Equals("contact.db", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsShard(fileName, "message") || IsShard(fileName, "media");
    }

    private static bool IsShard(string fileName, string prefix)
    {
        if (!fileName.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var number = fileName[(prefix.Length + 1)..^3];
        return int.TryParse(number, out var shard) && shard >= 0;
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}
