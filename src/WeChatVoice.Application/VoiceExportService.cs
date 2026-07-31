using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;

namespace WeChatVoice.Application;

/// <summary>
/// Coordinates source reads, raw SILK persistence, optional WAV decoding, and
/// manifest creation. It deliberately has no knowledge of database schemas or
/// the export directory layout.
/// </summary>
public sealed class VoiceExportService
{
    private const int CopyBufferSize = 81_920;

    private readonly IVoiceSource _voiceSource;
    private readonly IVoiceExportStore _exportStore;
    private readonly IVoiceDecoder? _voiceDecoder;

    public VoiceExportService(
        IVoiceSource voiceSource,
        IVoiceExportStore exportStore,
        IVoiceDecoder? voiceDecoder = null)
    {
        _voiceSource = voiceSource ?? throw new ArgumentNullException(nameof(voiceSource));
        _exportStore = exportStore ?? throw new ArgumentNullException(nameof(exportStore));
        _voiceDecoder = voiceDecoder;
    }

    public Task<VoiceExportManifest> ExportAsync(
        VoiceQuery query,
        CancellationToken cancellationToken = default)
        => ExportAsync(query, options: null, cancellationToken);

    /// <summary>
    /// Exports all messages returned by <paramref name="query"/>. Query and
    /// per-message errors are captured in the resulting manifest; caller
    /// cancellation is still propagated after a best-effort manifest write.
    /// </summary>
    public async Task<VoiceExportManifest> ExportAsync(
        VoiceQuery query,
        VoiceExportOptions? options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        options ??= new VoiceExportOptions();
        if (options.MaxDegreeOfParallelism <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxDegreeOfParallelism must be greater than zero.");
        }

        var entries = new ConcurrentQueue<VoiceExportEntry>();
        var failures = new ConcurrentQueue<VoiceExportFailure>();
        var activeExports = new List<Task>(options.MaxDegreeOfParallelism);
        var cancellationObserved = false;

        try
        {
            await foreach (var message in _voiceSource
                .QueryAsync(query, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (activeExports.Count >= options.MaxDegreeOfParallelism)
                {
                    await DrainOneAsync(activeExports, cancellationToken).ConfigureAwait(false);
                }

                activeExports.Add(ExportOneAsync(message, options, entries, failures, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationObserved = true;
        }
        catch (Exception exception)
        {
            failures.Enqueue(CreateFailure(messageId: null, stage: "query", exception));
        }
        finally
        {
            try
            {
                await Task.WhenAll(activeExports).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancellationObserved = true;
            }
        }

        var manifest = new VoiceExportManifest(
            DateTimeOffset.UtcNow,
            entries
                .OrderBy(static entry => entry.OccurredAtUtc)
                .ThenBy(static entry => entry.MessageId, StringComparer.Ordinal),
            failures
                .OrderBy(static failure => failure.MessageId ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static failure => failure.Stage, StringComparer.Ordinal)
                .ThenBy(static failure => failure.Error, StringComparer.Ordinal));

        // A cancellation token is intentionally not supplied here. The
        // partial-run manifest is what makes a cancelled export auditable.
        await _exportStore.WriteManifestAsync(manifest, CancellationToken.None).ConfigureAwait(false);

        if (cancellationObserved || cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return manifest;
    }

    private async Task DrainOneAsync(List<Task> activeExports, CancellationToken cancellationToken)
    {
        var completed = await Task.WhenAny(activeExports).ConfigureAwait(false);
        activeExports.Remove(completed);

        try
        {
            await completed.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller's cancellation will be observed by the enumeration or
            // the final Task.WhenAll, after which the manifest is still written.
        }
    }

    private async Task ExportOneAsync(
        VoiceMessage message,
        VoiceExportOptions options,
        ConcurrentQueue<VoiceExportEntry> entries,
        ConcurrentQueue<VoiceExportFailure> failures,
        CancellationToken cancellationToken)
    {
        VoiceExportPaths? paths = null;

        try
        {
            paths = await _exportStore.CreatePathsAsync(message, cancellationToken).ConfigureAwait(false);
            var copyResult = await CopyOriginalAsync(message, paths, cancellationToken).ConfigureAwait(false);

            string? decodedPath = null;
            if (options.DecodeToWav)
            {
                if (_voiceDecoder is null)
                {
                    failures.Enqueue(new VoiceExportFailure(
                        message.MessageId,
                        "decode",
                        "WAV decoding was requested but no voice decoder was configured.",
                        nameof(InvalidOperationException)));
                }
                else
                {
                    try
                    {
                        await _voiceDecoder
                            .DecodeAsync(paths.OriginalFilePath, paths.DecodedFilePath, cancellationToken)
                            .ConfigureAwait(false);
                        decodedPath = paths.DecodedManifestPath;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        failures.Enqueue(CreateFailure(message.MessageId, "decode", exception));
                    }
                }
            }

            entries.Enqueue(new VoiceExportEntry(
                message.MessageId,
                message.ConversationId,
                message.OccurredAtUtc,
                message.Direction,
                paths.OriginalManifestPath,
                copyResult.ByteLength,
                copyResult.Sha256,
                decodedPath));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            failures.Enqueue(CreateFailure(message.MessageId, "export", exception));
        }
    }

    private async Task<PayloadCopyResult> CopyOriginalAsync(
        VoiceMessage message,
        VoiceExportPaths paths,
        CancellationToken cancellationToken)
    {
        await using var input = await _voiceSource
            .OpenPayloadAsync(message, cancellationToken)
            .ConfigureAwait(false);

        if (!input.CanRead)
        {
            throw new InvalidOperationException("The voice source returned a non-readable payload stream.");
        }

        var outputDirectory = Path.GetDirectoryName(paths.OriginalFilePath)
            ?? throw new ArgumentException("The original export path must include a directory.", nameof(paths));
        Directory.CreateDirectory(outputDirectory);
        var temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(paths.OriginalFilePath)}.{Guid.NewGuid():N}.tmp");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long byteLength = 0;

        try
        {
            PayloadCopyResult result;
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (true)
                {
                    var count = await input.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken).ConfigureAwait(false);
                    if (count == 0)
                    {
                        break;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, count);
                    byteLength = checked(byteLength + count);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                result = new PayloadCopyResult(byteLength, Convert.ToHexString(hash.GetHashAndReset()));
            }

            // Do not overwrite a pre-existing file. The store reserves a unique
            // name, while this move prevents a partially copied SILK file from
            // ever becoming visible at that final name.
            File.Move(temporaryPath, paths.OriginalFilePath);
            return result;
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Preserve the copy result or failure. The unique sibling name
            // makes a residual temporary file safe to identify and remove.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the copy result or failure.
        }
    }

    private static VoiceExportFailure CreateFailure(string? messageId, string stage, Exception exception)
        => new(
            messageId,
            stage,
            string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message,
            exception.GetType().FullName);

    private sealed record PayloadCopyResult(long ByteLength, string Sha256);
}
