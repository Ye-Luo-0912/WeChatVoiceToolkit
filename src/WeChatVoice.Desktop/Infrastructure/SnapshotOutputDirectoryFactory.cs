using System.Security.Cryptography;
using System.Text;

namespace WeChatVoice.Desktop.Infrastructure;

/// <summary>
/// Creates non-colliding, private default snapshot destinations. The account
/// component is an opaque digest; neither the account candidate nor source
/// path is copied into the generated directory name.
/// </summary>
public sealed class SnapshotOutputDirectoryFactory
{
    private readonly string _applicationDataRoot;

    public SnapshotOutputDirectoryFactory(string applicationDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataRoot);
        _applicationDataRoot = Path.GetFullPath(applicationDataRoot);
    }

    public string CreateDefault(string sourceDirectory, string? accountCandidate, string accountDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountDirectory);

        var source = Path.GetFullPath(sourceDirectory);
        var fingerprint = CreateAccountFingerprint(accountCandidate, accountDirectory);
        var operation = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N");

        foreach (var root in GetCandidateRoots())
        {
            var output = Path.Combine(root, fingerprint, operation);
            if (IsDisjoint(output, source)
                && IsSafeParent(root)
                && !Directory.Exists(output)
                && !File.Exists(output))
            {
                return output;
            }
        }

        throw new InvalidOperationException("A safe default snapshot destination could not be allocated.");
    }

    public static string CreateAccountFingerprint(string? accountCandidate, string accountDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountDirectory);
        var identity = string.IsNullOrWhiteSpace(accountCandidate) ? accountDirectory : accountCandidate;
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes("wechatvoice-account-v1|" + identity.Trim().ToUpperInvariant())))
            .ToLowerInvariant()[..32];
    }

    private IEnumerable<string> GetCandidateRoots()
    {
        yield return Path.Combine(_applicationDataRoot, "Data", "Snapshots");

        var temporaryRoot = Path.Combine(Path.GetTempPath(), "WeChatVoiceToolkit", "Snapshots");
        if (!string.Equals(temporaryRoot, _applicationDataRoot, StringComparison.OrdinalIgnoreCase))
        {
            yield return temporaryRoot;
        }
    }

    private static bool IsSafeParent(string path)
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

        try
        {
            var current = new DirectoryInfo(existing);
            while (current is not null)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                current = current.Parent;
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsDisjoint(string candidate, string source)
        => !IsSameOrChild(candidate, source) && !IsSameOrChild(source, candidate);

    private static bool IsSameOrChild(string candidate, string root)
    {
        var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
