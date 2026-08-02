namespace WeChatVoice.Desktop.Infrastructure;

public sealed record DesktopPathValidationResult(
    bool IsValid,
    string? Error,
    long? AvailableFreeBytes = null)
{
    public static DesktopPathValidationResult Valid(long? availableFreeBytes = null)
        => new(true, null, availableFreeBytes);

    public static DesktopPathValidationResult Invalid(string error)
        => new(false, error, null);
}

/// <summary>
/// Cheap, UI-facing preflight for paths. The workflow remains authoritative;
/// this validator only gives immediate feedback before a high-cost operation.
/// It never opens a database or follows a reparse point.
/// </summary>
public static class DesktopPathValidator
{
    public static DesktopPathValidationResult ValidateSnapshotPaths(string? sourceDirectory, string? outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || string.IsNullOrWhiteSpace(outputDirectory))
        {
            return DesktopPathValidationResult.Invalid("请选择源目录和快照输出目录。");
        }

        string source;
        string output;
        try
        {
            source = Path.GetFullPath(sourceDirectory);
            output = Path.GetFullPath(outputDirectory);
        }
        catch (ArgumentException)
        {
            return DesktopPathValidationResult.Invalid("目录路径格式无效。");
        }

        if (!Directory.Exists(source))
        {
            return DesktopPathValidationResult.Invalid("源目录不存在。");
        }

        if (ContainsReparsePoint(source))
        {
            return DesktopPathValidationResult.Invalid("源目录或其父路径包含 Reparse Point，不能用于快照。");
        }

        if (IsSameOrChild(output, source) || IsSameOrChild(source, output))
        {
            return DesktopPathValidationResult.Invalid("源目录和快照输出目录不能相互包含。");
        }

        if (File.Exists(output))
        {
            return DesktopPathValidationResult.Invalid("快照输出路径已经是文件。");
        }

        if (Directory.Exists(output))
        {
            if (ContainsReparsePoint(output))
            {
                return DesktopPathValidationResult.Invalid("快照输出目录包含 Reparse Point。");
            }

            try
            {
                if (Directory.EnumerateFileSystemEntries(output).Any())
                {
                    return DesktopPathValidationResult.Invalid("快照输出目录必须为空。");
                }
            }
            catch (IOException)
            {
                return DesktopPathValidationResult.Invalid("无法读取快照输出目录。");
            }
            catch (UnauthorizedAccessException)
            {
                return DesktopPathValidationResult.Invalid("没有读取快照输出目录的权限。");
            }
        }

        return DesktopPathValidationResult.Valid(TryGetAvailableFreeBytes(output));
    }

    private static bool ContainsReparsePoint(string path)
    {
        var current = new DirectoryInfo(path);
        while (current is not null)
        {
            try
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private static long? TryGetAvailableFreeBytes(string path)
    {
        try
        {
            var existing = path;
            while (!Directory.Exists(existing))
            {
                var parent = Path.GetDirectoryName(existing);
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, existing, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                existing = parent;
            }

            var root = Path.GetPathRoot(existing);
            return string.IsNullOrWhiteSpace(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsSameOrChild(string candidate, string root)
    {
        var normalizedCandidate = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
