namespace WeChatVoice.Infrastructure.Sqlite;

/// <summary>
/// Configures Microsoft.Data.Sqlite.Core to use the SQLite library supplied by
/// Windows rather than a bundled native SQLite binary.
/// </summary>
internal static class WindowsSqliteProvider
{
    private static readonly object SyncRoot = new();
    private static bool _initialized;

    internal static void EnsureInitialized()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "SQLite schema inspection is configured to use the Windows winsqlite3 provider and is supported only on Windows.");
        }

        lock (SyncRoot)
        {
            if (_initialized)
            {
                return;
            }

            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
            SQLitePCL.raw.FreezeProvider();
            _initialized = true;
        }
    }
}
