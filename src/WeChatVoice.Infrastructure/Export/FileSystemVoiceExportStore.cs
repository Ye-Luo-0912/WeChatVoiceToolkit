using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Infrastructure.Export;

/// <summary>
/// A local export store that keeps original SILK and derived WAV artifacts in
/// separate, date-based directory trees.
/// </summary>
public sealed class FileSystemVoiceExportStore : IVoiceExportStore
{
    private readonly ConcurrentDictionary<string, byte> _reservedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _exportRoot;

    public FileSystemVoiceExportStore(string exportRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportRoot);
        _exportRoot = Path.GetFullPath(exportRoot);
    }

    /// <summary>
    /// Absolute root under which all original, decoded, and manifest artifacts
    /// are stored. The directory is created lazily when an artifact is written.
    /// </summary>
    public string ExportRoot => _exportRoot;

    /// <summary>
    /// Creates unique, as-yet nonexistent destinations for an export. It creates
    /// only their parent directories; it never creates or truncates media files.
    /// </summary>
    public ValueTask<VoiceExportPaths> CreatePathsAsync(VoiceMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        var occurredAtUtc = message.OccurredAtUtc.ToUniversalTime();
        var year = occurredAtUtc.ToString("yyyy", CultureInfo.InvariantCulture);
        var month = occurredAtUtc.ToString("MM", CultureInfo.InvariantCulture);
        var sourceId = ExportPathSafety.SanitizeFileStem(message.MessageId, "voice");
        var stableSuffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message.MessageId)))
            .ToLowerInvariant()[..12];
        var baseName = $"{sourceId[..Math.Min(sourceId.Length, 80)]}-{stableSuffix}";

        for (var attempt = 0; attempt < int.MaxValue; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = attempt == 0 ? baseName : $"{baseName}-{attempt:D4}";
            var originalManifestPath = $"original/{year}/{month}/{fileName}.silk";
            var decodedManifestPath = $"decoded/{year}/{month}/{fileName}.wav";
            var originalPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "original", year, month, $"{fileName}.silk");
            var decodedPath = ExportPathSafety.CombineUnderRoot(_exportRoot, "decoded", year, month, $"{fileName}.wav");

            if (File.Exists(originalPath) || File.Exists(decodedPath))
            {
                continue;
            }

            if (!_reservedPaths.TryAdd(originalPath, 0))
            {
                continue;
            }

            if (!_reservedPaths.TryAdd(decodedPath, 0))
            {
                _reservedPaths.TryRemove(originalPath, out _);
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(decodedPath)!);
                return ValueTask.FromResult(new VoiceExportPaths(
                    originalPath,
                    decodedPath,
                    originalManifestPath,
                    decodedManifestPath));
            }
            catch
            {
                _reservedPaths.TryRemove(originalPath, out _);
                _reservedPaths.TryRemove(decodedPath, out _);
                throw;
            }
        }

        throw new IOException("A unique export file name could not be allocated.");
    }

    /// <summary>
    /// Streams an original SILK payload through a sibling temporary file before
    /// it becomes visible at the allocated destination.
    /// </summary>
    public Task WriteOriginalAsync(VoiceExportPaths paths, Stream source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        EnsureOwnedPath(paths.OriginalFilePath, ".silk");
        return AtomicFileWriter.CopyStreamAsync(source, paths.OriginalFilePath, overwrite: false, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Streams a derived WAV payload through a sibling temporary file before it
    /// becomes visible at the allocated destination.
    /// </summary>
    public Task WriteDecodedAsync(VoiceExportPaths paths, Stream source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        EnsureOwnedPath(paths.DecodedFilePath, ".wav");
        return AtomicFileWriter.CopyStreamAsync(source, paths.DecodedFilePath, overwrite: false, cancellationToken: cancellationToken);
    }

    public Task WriteManifestAsync(VoiceExportManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return AtomicFileWriter.WriteJsonAsync(
            ExportPathSafety.CombineUnderRoot(_exportRoot, "manifest.json"),
            manifest,
            InfrastructureJson.Indented,
            cancellationToken);
    }

    private void EnsureOwnedPath(string path, string expectedExtension)
    {
        if (!string.Equals(Path.GetExtension(path), expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"The path must use the {expectedExtension} extension.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        var rootWithSeparator = _exportRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _exportRoot
            : _exportRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The export path is outside this store's export root.");
        }
    }
}
