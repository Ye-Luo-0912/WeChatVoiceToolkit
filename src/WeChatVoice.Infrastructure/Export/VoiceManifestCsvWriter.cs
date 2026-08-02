using System.Globalization;
using System.Text;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Infrastructure.Export;

/// <summary>
/// Writes the portable per-item training manifest beside the JSON manifest.
/// Only provenance and artifact metadata are included; contact usernames and
/// database content never enter this file.
/// </summary>
internal static class VoiceManifestCsvWriter
{
    private static readonly string[] Header =
    [
        "message_id",
        "conversation_id",
        "occurred_at_utc",
        "direction",
        "original_path",
        "original_byte_length",
        "original_sha256",
        "duration_ms",
        "source_stable_key",
        "source_database",
        "shard_id",
        "speaker_id",
        "was_skipped",
        "selected_for_training",
        "has_decode_error",
        "quality_flags"
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
                entry.MessageId,
                entry.ConversationId,
                entry.OccurredAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                entry.Direction.ToString(),
                entry.OriginalPath,
                entry.OriginalByteLength.ToString(CultureInfo.InvariantCulture),
                entry.OriginalSha256,
                entry.DurationMs?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                entry.SourceStableKey,
                entry.SourceDatabase,
                entry.ShardId,
                entry.SpeakerId,
                entry.WasSkipped.ToString(CultureInfo.InvariantCulture),
                entry.SelectedForTraining.ToString(CultureInfo.InvariantCulture),
                entry.HasDecodeError.ToString(CultureInfo.InvariantCulture),
                string.Join('|', entry.QualityFlags),
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

            var value = values[index] ?? string.Empty;
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
}
