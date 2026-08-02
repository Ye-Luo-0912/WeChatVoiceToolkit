namespace WeChatVoice.Desktop.Infrastructure;

public sealed record WeixinDataSourceCandidate(
    string AccountDirectory,
    string? AccountCandidate,
    string DbStoragePath,
    DateTimeOffset LastWriteTimeUtc,
    int DatabaseCount,
    bool IsReparsePoint,
    bool HasSnapshot);

/// <summary>Discovers directory candidates only; it never infers a schema.</summary>
public sealed class WeixinDataSourceDiscovery
{
    public IReadOnlyList<WeixinDataSourceCandidate> Discover(IEnumerable<string>? roots = null)
    {
        var searchRoots = (roots ?? [
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)])
            .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<WeixinDataSourceCandidate>();
        foreach (var root in searchRoots)
        {
            var pending = new Stack<string>([root]);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                try
                {
                    var info = new DirectoryInfo(current);
                    var account = info.Parent?.FullName ?? current;
                    var attributes = info.Attributes;
                    if (info.Name.Equals("db_storage", StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add(new WeixinDataSourceCandidate(
                        account,
                        info.Parent?.Name.StartsWith("wxid_", StringComparison.OrdinalIgnoreCase) == true ? info.Parent.Name : null,
                        info.FullName,
                        info.LastWriteTimeUtc,
                        Directory.EnumerateFiles(current, "*.db", SearchOption.TopDirectoryOnly).Count(),
                        (attributes & FileAttributes.ReparsePoint) != 0,
                        File.Exists(Path.Combine(current, ".wechatvoice", "snapshot-manifest.json"))));
                    }
                    if ((attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        foreach (var child in Directory.EnumerateDirectories(current)) pending.Push(child);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return candidates.OrderBy(item => item.DbStoragePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
