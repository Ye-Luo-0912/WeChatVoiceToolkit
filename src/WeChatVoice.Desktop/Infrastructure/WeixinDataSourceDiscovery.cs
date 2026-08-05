using System.Diagnostics;
using System.Security;
using Microsoft.Win32;
using WeChatVoice.Core.Models;

namespace WeChatVoice.Desktop.Infrastructure;

public sealed record WeixinDataSourceDiscoveryOptions(
    int MaxDepth = 8,
    int MaxDirectories = 20_000,
    TimeSpan? Timeout = null)
{
    public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromSeconds(5);
}

public sealed record WeixinDataSourceDiscoveryResult(
    IReadOnlyList<WeixinDataSourceCandidate> Candidates,
    bool WasTruncated,
    int VisitedDirectoryCount);

public sealed record WeixinDataSourceCandidate(
    string AccountDirectory,
    string? AccountCandidate,
    string DbStoragePath,
    DateTimeOffset LastWriteTimeUtc,
    int DatabaseCount,
    bool IsReparsePoint,
    bool HasSnapshot)
{
    public bool IsSelectable => !IsReparsePoint && DatabaseCount > 0 && !string.IsNullOrWhiteSpace(AccountCandidate);

    public string? UnavailableReason
        => IsSelectable
            ? null
            : IsReparsePoint
                ? "目录包含 Reparse Point"
                : DatabaseCount <= 0
                    ? "未找到数据库文件"
                    : "无法从固定账号目录布局推导账号候选";
}

public interface IWeixinDataSourceDiscovery
{
    IReadOnlyList<WeixinDataSourceCandidate> Discover(
        IEnumerable<string>? roots = null,
        WeixinDataSourceDiscoveryOptions? options = null);

    Task<IReadOnlyList<WeixinDataSourceCandidate>> DiscoverAsync(
        IEnumerable<string>? roots = null,
        WeixinDataSourceDiscoveryOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<WeixinDataSourceDiscoveryResult> DiscoverDetailedAsync(
        IEnumerable<string>? roots = null,
        WeixinDataSourceDiscoveryOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Discovers directory candidates only; it never infers a schema. The search
/// is intentionally bounded because an unrestricted AppData walk is both
/// surprising for users and a poor UI operation under a damaged profile.
/// </summary>
public sealed class WeixinDataSourceDiscovery : IWeixinDataSourceDiscovery
{
    private readonly RecentWorkspaceStore _recentWorkspaces;

    public WeixinDataSourceDiscovery(RecentWorkspaceStore? recentWorkspaces = null)
        => _recentWorkspaces = recentWorkspaces ?? new RecentWorkspaceStore();

    public IReadOnlyList<WeixinDataSourceCandidate> Discover(
        IEnumerable<string>? roots = null,
        WeixinDataSourceDiscoveryOptions? options = null)
        => DiscoverDetailed(roots, options, CancellationToken.None).Candidates;

    public Task<IReadOnlyList<WeixinDataSourceCandidate>> DiscoverAsync(
        IEnumerable<string>? roots = null,
        WeixinDataSourceDiscoveryOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () => DiscoverDetailed(roots, options, cancellationToken).Candidates,
            cancellationToken);

    public Task<WeixinDataSourceDiscoveryResult> DiscoverDetailedAsync(
        IEnumerable<string>? roots = null,
        WeixinDataSourceDiscoveryOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () => DiscoverDetailed(roots, options, cancellationToken),
            cancellationToken);

    public WeixinDataSourceDiscoveryResult DiscoverDetailed(
        IEnumerable<string>? roots = null,
        WeixinDataSourceDiscoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new WeixinDataSourceDiscoveryOptions();
        if (options.MaxDepth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxDepth cannot be negative.");
        }

        if (options.MaxDirectories <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxDirectories must be positive.");
        }

        if (options.EffectiveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The discovery timeout must be positive.");
        }

        var deadline = Stopwatch.GetTimestamp() +
            (long)(Stopwatch.Frequency * options.EffectiveTimeout.TotalSeconds);
        var searchRoots = (roots ?? GetDefaultRoots())
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var candidates = new List<WeixinDataSourceCandidate>();
        var pending = new Stack<(string Path, int Depth)>();
        var visitedDirectories = 0;
        var wasTruncated = false;

        foreach (var root in searchRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            pending.Push((root, 0));
            while (pending.Count > 0)
            {
                if (!CanContinue(cancellationToken, deadline, options.MaxDirectories, visitedDirectories))
                {
                    wasTruncated = true;
                    pending.Clear();
                    break;
                }

                var (current, depth) = pending.Pop();
                DirectoryInfo info;
                try
                {
                    info = new DirectoryInfo(current);
                    if (!info.Exists)
                    {
                        continue;
                    }
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                visitedDirectories++;
                var isReparsePoint = (info.Attributes & FileAttributes.ReparsePoint) != 0;
                if (info.Name.Equals(".wechatvoice", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (info.Name.Equals("db_storage", StringComparison.OrdinalIgnoreCase))
                {
                    var databaseTree = CountDatabaseFiles(info, depth, options, cancellationToken, deadline, ref visitedDirectories, ref wasTruncated);
                    var identity = SnapshotSourceIdentity.TryDerive(info.FullName, Array.Empty<SnapshotFileRecord>());
                    candidates.Add(new WeixinDataSourceCandidate(
                        info.Parent?.FullName ?? info.FullName,
                        identity?.AccountCandidate,
                        info.FullName,
                        databaseTree.LastWriteTimeUtc,
                        databaseTree.DatabaseCount,
                        isReparsePoint,
                        _recentWorkspaces.HasSnapshotForSource(info.FullName)));
                    continue;
                }

                if (isReparsePoint || depth >= options.MaxDepth)
                {
                    continue;
                }

                try
                {
                    foreach (var child in info.EnumerateDirectories())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if ((child.Attributes & FileAttributes.ReparsePoint) == 0)
                        {
                            pending.Push((child.FullName, depth + 1));
                        }
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        var ordered = candidates
            .GroupBy(item => item.DbStoragePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.LastWriteTimeUtc).First())
            .OrderBy(item => item.DbStoragePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new WeixinDataSourceDiscoveryResult(ordered, wasTruncated, visitedDirectories);
    }

    private static DatabaseTreeStatistics CountDatabaseFiles(
        DirectoryInfo root,
        int rootDepth,
        WeixinDataSourceDiscoveryOptions options,
        CancellationToken cancellationToken,
        long deadline,
        ref int visitedDirectories,
        ref bool wasTruncated)
    {
        var pending = new Stack<(DirectoryInfo Directory, int Depth)>([(root, rootDepth)]);
        var count = 0;
        var lastWrite = root.LastWriteTimeUtc;
        while (pending.Count > 0)
        {
            if (!CanContinue(cancellationToken, deadline, options.MaxDirectories, visitedDirectories))
            {
                wasTruncated = true;
                break;
            }

            var (current, depth) = pending.Pop();
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            if (!ReferenceEquals(current, root))
            {
                visitedDirectories++;
            }

            try
            {
                if (current.Name.Equals(".wechatvoice", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                lastWrite = Max(lastWrite, current.LastWriteTimeUtc);
                foreach (var file in current.EnumerateFiles("*.db", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    count++;
                    lastWrite = Max(lastWrite, file.LastWriteTimeUtc);
                }

                foreach (var child in current.EnumerateDirectories())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((child.Attributes & FileAttributes.ReparsePoint) == 0 && depth < options.MaxDepth)
                    {
                        pending.Push((child, depth + 1));
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return new DatabaseTreeStatistics(count, new DateTimeOffset(lastWrite, TimeSpan.Zero));
    }

    private static bool CanContinue(
        CancellationToken cancellationToken,
        long deadline,
        int maxDirectories,
        int visitedDirectories)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return visitedDirectories < maxDirectories && Stopwatch.GetTimestamp() < deadline;
    }

    private static DateTime Max(DateTime left, DateTime right)
        => left >= right ? left : right;

    private static IEnumerable<string> GetDefaultRoots()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("WECHATVOICE_WEIXIN_DATA_ROOT");
        var userName = Environment.UserName;
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        // The modern xwechat_files roots are first. The registry and fixed-drive
        // candidates cover installations whose Windows profile or Weixin data
        // was moved to another volume; users should not have to know that path.
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            roots.Add(configuredRoot);
        }

        roots.AddRange(ReadRegistryDataRoots(documents));
        roots.AddRange(
        [
            Path.Combine(userProfile, "AppData", "Local", "Programs", "xwechat_files"),
            Path.Combine(userProfile, "AppData", "Local", "xwechat_files"),
            Path.Combine(userProfile, "AppData", "Roaming", "Tencent", "xwechat"),
            Path.Combine(userProfile, "AppData", "Roaming", "Tencent", "Weixin"),
            Path.Combine(documents, "xwechat_files"),
            Path.Combine(documents, "Weixin Files"),
            Path.Combine(localAppData, "Tencent", "xwechat_files"),
            Path.Combine(appData, "Tencent", "xwechat_files"),
            Path.Combine(documents, "WeChat Files"),
            Path.Combine(localAppData, "Programs", "xwechat_files"),
            Path.Combine(localAppData, "xwechat_files"),
            Path.Combine(localAppData, "Tencent", "WeChat"),
            Path.Combine(appData, "Tencent", "WeChat"),
        ]);

        // Some Weixin installers put the per-user data under a same-named
        // profile on a non-system fixed drive. This is an exact path probe,
        // not a recursive drive scan.
        if (OperatingSystem.IsWindows())
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                    {
                        continue;
                    }

                    var alternateProfile = Path.Combine(drive.RootDirectory.FullName, "Users", userName);
                    roots.Add(Path.Combine(alternateProfile, "AppData", "Local", "Programs", "xwechat_files"));
                    roots.Add(Path.Combine(alternateProfile, "AppData", "Local", "xwechat_files"));
                    roots.Add(Path.Combine(alternateProfile, "AppData", "Roaming", "Tencent", "xwechat"));
                    roots.Add(Path.Combine(alternateProfile, "Documents", "xwechat_files"));
                    roots.Add(Path.Combine(alternateProfile, "Documents", "Weixin Files"));
                    roots.Add(Path.Combine(alternateProfile, "Documents", "WeChat Files"));
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        return roots;
    }

    private static IEnumerable<string> ReadRegistryDataRoots(string documents)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        string[] keyNames =
        [
            @"Software\Tencent\Weixin",
            @"Software\Tencent\WeChat",
            @"Software\Tencent\xwechat",
        ];
        string[] valueNames = ["FileSavePath", "OldFileSavePath", "DataPath", "DataRoot", "StoragePath"];

        foreach (var keyName in keyNames)
        {
            RegistryKey? key = null;
            try
            {
                key = Registry.CurrentUser.OpenSubKey(keyName, writable: false);
            }
            catch (SecurityException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            using (key)
            {
                if (key is null)
                {
                    continue;
                }

                foreach (var valueName in valueNames)
                {
                    if (key.GetValue(valueName) is not string raw || string.IsNullOrWhiteSpace(raw))
                    {
                        continue;
                    }

                    var value = Environment.ExpandEnvironmentVariables(raw.Trim());
                    if (value.Equals("MyDocument:", StringComparison.OrdinalIgnoreCase)
                        || value.Equals("MyDocuments:", StringComparison.OrdinalIgnoreCase)
                        || value.Equals("MyDocument", StringComparison.OrdinalIgnoreCase))
                    {
                        yield return documents;
                        continue;
                    }

                    if (Path.IsPathRooted(value))
                    {
                        yield return value;
                    }
                }
            }
        }
    }

    private sealed record DatabaseTreeStatistics(int DatabaseCount, DateTimeOffset LastWriteTimeUtc);
}
