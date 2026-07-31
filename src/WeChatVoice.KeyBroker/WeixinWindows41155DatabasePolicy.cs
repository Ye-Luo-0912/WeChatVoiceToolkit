namespace WeChatVoice.KeyBroker;

/// <summary>
/// Exact source coverage policy for the observed 4.1.11.55 data layout. The
/// migration-only unsupported-message database is not required by the voice
/// export chain and may be recorded as intentionally ignored when its key is
/// not present in the running process. No other database receives this
/// exception.
/// </summary>
internal static class WeixinWindows41155DatabasePolicy
{
    internal const string OptionalMigrationDatabase = "migrate/unspportmsg.db";

    internal static bool CanIntentionallyIgnore(string relativePath) =>
        string.Equals(Normalize(relativePath), OptionalMigrationDatabase, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => path.Replace('\\', '/');
}
