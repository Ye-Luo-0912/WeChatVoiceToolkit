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
        if (string.IsNullOrWhiteSpace(manifest.DatasetNamespaceKey))
        {
            throw new InvalidDataException("A portable export CSV requires a dataset namespace key.");
        }

        return AtomicFileWriter.WriteStreamAsync(
            destinationPath,
            async (stream, token) =>
            {
                await using var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 64 * 1024,
                    leaveOpen: true);
                await writer.WriteLineAsync(BuildRow(Header)).ConfigureAwait(false);
                foreach (var entry in manifest.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    await writer.WriteLineAsync(BuildRow(
                    [
                        ExportItemIdentity.ComputeItemId(entry, manifest.DatasetNamespaceKey),
                        entry.OriginalPath,
                        entry.OriginalSha256,
                        entry.DurationMs?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                        entry.OriginalByteLength.ToString(CultureInfo.InvariantCulture),
                        string.Join('|', entry.QualityFlags),
                        entry.TrainingEligibility.ToString(),
                        (entry.UserSelectionState == UserSelectionState.Selected).ToString(CultureInfo.InvariantCulture),
                    ])).ConfigureAwait(false);
                }

                await writer.FlushAsync(token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public static Task WritePortableAsync(
        string destinationPath,
        VoiceDatasetManifest manifest,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(manifest);
        return AtomicFileWriter.WriteStreamAsync(
            destinationPath,
            async (stream, token) =>
            {
                await using var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 64 * 1024,
                    leaveOpen: true);
                await writer.WriteLineAsync(BuildRow(Header)).ConfigureAwait(false);
                foreach (var entry in manifest.Items)
                {
                    token.ThrowIfCancellationRequested();
                    await writer.WriteLineAsync(BuildRow(
                    [
                        entry.ItemId,
                        entry.RelativeAudioPath,
                        entry.Sha256,
                        entry.DurationMs?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                        entry.ByteLength.ToString(CultureInfo.InvariantCulture),
                        string.Join('|', entry.QualityFlags),
                        entry.TrainingEligibility.ToString(),
                        entry.Selected.ToString(CultureInfo.InvariantCulture),
                    ])).ConfigureAwait(false);
                }

                await writer.FlushAsync(token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    private static string BuildRow(IReadOnlyList<string?> values)
    {
        var builder = new StringBuilder(192);
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

        return builder.ToString();
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
