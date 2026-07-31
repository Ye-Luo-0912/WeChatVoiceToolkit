using System.Text.RegularExpressions;

namespace WeChatVoice.Infrastructure.Materialization;

/// <summary>
/// Keeps diagnostics useful without allowing a development backend to smuggle
/// key material, salts, tokens, or absolute local paths into an exception or
/// Journal. Formal Key Broker failures use structured codes and do not rely on
/// this best-effort redaction.
/// </summary>
internal static partial class SensitiveOutputRedactor
{
    [GeneratedRegex(@"(?i)(key|secret|salt|token|password)\s*[:=]\s*[^\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex(@"(?i)([A-Z]:\\|\\\\)[^\r\n""']+", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathRegex();

    internal static string? Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var redacted = SecretAssignmentRegex().Replace(value, "$1=<redacted>");
        return WindowsPathRegex().Replace(redacted, "<local-path>");
    }
}
