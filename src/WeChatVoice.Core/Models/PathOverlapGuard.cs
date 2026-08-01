namespace WeChatVoice.Core.Models;

public static class PathOverlapGuard
{
    public static void EnsureDisjoint(params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var normalized = paths.Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToArray();
        for (var i = 0; i < normalized.Length; i++)
        {
            for (var j = i + 1; j < normalized.Length; j++)
            {
                if (string.Equals(normalized[i], normalized[j], StringComparison.OrdinalIgnoreCase)
                    || Contains(normalized[i], normalized[j])
                    || Contains(normalized[j], normalized[i]))
                {
                    throw new InvalidDataException($"The paths '{normalized[i]}' and '{normalized[j]}' must not contain one another.");
                }
            }
        }
    }

    private static bool Contains(string parent, string child)
    {
        var prefix = parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar;
        return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
