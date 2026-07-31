namespace WeChatVoice.Infrastructure.Export;

internal static class ExportPathSafety
{
    internal static string SanitizeFileStem(string? value, string fallback)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(candidate.Length);

        foreach (var character in candidate)
        {
            builder.Append(invalidCharacters.Contains(character) ? '_' : character);
        }

        var sanitized = builder.ToString().Trim().Trim('.');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = fallback;
        }

        return sanitized.Length <= 120 ? sanitized : sanitized[..120];
    }

    internal static string CombineUnderRoot(string rootPath, params string[] segments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(segments);

        var root = Path.GetFullPath(rootPath);
        var candidate = Path.GetFullPath(Path.Combine([root, .. segments]));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The generated export path escaped its configured root.");
        }

        return candidate;
    }
}
