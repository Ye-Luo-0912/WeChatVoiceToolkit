using System.Globalization;
using System.Text;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Infrastructure.Export;

/// <summary>
/// Writes the portable dataset manifest beside the private JSON audit
/// manifest. No contact username, source database, source stable key, or
/// local path is emitted here.
/// </summary>
internal static class VoiceManifestCsvWriter
{
    private static readonly string[] Header =
    [
        "item_id",
        "relative_audio_path",
        "sha256",
        "duration_ms",
        "byte_length",
        "quality_flags",
        "training_eligibility",
        "selected"
    ];

    public static Task WriteAsync(
        string destinationPath,
        VoiceExportManifest manifest,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(manifest);
        var builder = new StringBuilder(Math.Max(512, manifest.Entries.Count * 180));
        AppendRow(builder, Header);
        foreach (var entry in manifest.Entries)
        {
            AppendRow(builder,
            [
                ExportItemIdentity.ComputeItemId(entry),
                entry.OriginalPath,
                entry.OriginalSha256,
                entry.DurationMs?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                entry.OriginalByteLength.ToString(CultureInfo.InvariantCulture),
                string.Join('|', entry.QualityFlags),
                entry.TrainingEligibility.ToString(),
                (entry.UserSelectionState == UserSelectionState.Selected).ToString(CultureInfo.InvariantCulture),
            ]);
        }

        return AtomicFileWriter.WriteTextAsync(destinationPath, builder.ToString(), cancellationToken);
    }

    private static void AppendRow(StringBuilder builder, IReadOnlyList<string?> values)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            var value = SanitizeForSpreadsheet(values[index] ?? string.Empty);
            if (value.IndexOfAny([',', '"', '\r', '\n']) >= 0)
            {
                builder.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            }
            else
            {
                builder.Append(value);
            }
        }

        builder.AppendLine();
    }

    private static string SanitizeForSpreadsheet(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
        {
            return "'" + value;
        }

        return value;
    }
}
