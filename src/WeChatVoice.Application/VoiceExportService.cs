using System.Buffers;
using System.Collections.Concurrent;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;

namespace WeChatVoice.Application;

/// <summary>
/// Coordinates catalog reads, lease-owned artifact persistence, optional WAV
/// decoding, and manifest creation. It never interprets physical output paths.
/// </summary>
public sealed class VoiceExportService
{
    private const int CopyBufferSize = 81_920;

    private readonly IVoiceCatalog _voiceCatalog;
    private readonly IVoiceExportStore _exportStore;
    private readonly IVoiceDecoder? _voiceDecoder;

    public VoiceExportService(
        IVoiceCatalog voiceCatalog,
        IVoiceExportStore exportStore,
        IVoiceDecoder? voiceDecoder = null)
    {
        _voiceCatalog = voiceCatalog ?? throw new ArgumentNullException(nameof(voiceCatalog));
        _exportStore = exportStore ?? throw new ArgumentNullException(nameof(exportStore));
        _voiceDecoder = voiceDecoder;
    }

    [Obsolete("Use the IVoiceCatalog constructor.")]
    public VoiceExportService(
        IVoiceSource voiceSource,
        IVoiceExportStore exportStore,
        IVoiceDecoder? voiceDecoder = null)
        : this(new LegacyVoiceCatalog(voiceSource ?? throw new ArgumentNullException(nameof(voiceSource))), exportStore, voiceDecoder)
    {
    }

    public Task<VoiceExportManifest> ExportAsync(
        VoiceQuery query,
        CancellationToken cancellationToken = default)
        => ExportAsync(query, options: null, cancellationToken);

    public async Task<VoiceExportManifest> ExportAsync(
        VoiceQuery query,
        VoiceExportOptions? options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        options ??= new VoiceExportOptions();
        if (options.MaxDegreeOfParallelism <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxDegreeOfParallelism must be greater than zero.");
        }

        var entries = new ConcurrentQueue<VoiceExportEntry>();
        var failures = new ConcurrentQueue<VoiceExportFailure>();
        var activeExports = new List<Task>(options.MaxDegreeOfParallelism);
        var cancellationObserved = false;

        try
        {
            await foreach (var record in _voiceCatalog
                .QueryVoicesAsync(query, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (activeExports.Count >= options.MaxDegreeOfParallelism)
                {
                    await DrainOneAsync(activeExports, cancellationToken).ConfigureAwait(false);
                }

                activeExports.Add(ExportOneAsync(record, options, entries, failures, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationObserved = true;
        }
        catch (Exception exception)
        {
            failures.Enqueue(CreateFailure(null, "query", exception));
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
            entries.OrderBy(static entry => entry.OccurredAtUtc).ThenBy(static entry => entry.MessageId, StringComparer.Ordinal),
            failures.OrderBy(static failure => failure.MessageId ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static failure => failure.Stage, StringComparer.Ordinal)
                .ThenBy(static failure => failure.Error, StringComparer.Ordinal));

        await _exportStore.FinalizeRunAsync(manifest, CancellationToken.None).ConfigureAwait(false);
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
        }
    }

    private async Task ExportOneAsync(
        VoiceRecord record,
        VoiceExportOptions options,
        ConcurrentQueue<VoiceExportEntry> entries,
        ConcurrentQueue<VoiceExportFailure> failures,
        CancellationToken cancellationToken)
    {
        IExportItemLease? lease = null;
        try
        {
            lease = await _exportStore.BeginItemAsync(record, ExportExistingPolicy.Fail, cancellationToken).ConfigureAwait(false);
            if (lease.IsSkipped)
            {
                return;
            }

            var originalArtifact = await CopyOriginalAsync(record, lease, cancellationToken).ConfigureAwait(false);
            string? decodedPath = null;
            if (options.DecodeToWav)
            {
                if (_voiceDecoder is null)
                {
                    failures.Enqueue(new VoiceExportFailure(
                        record.MessageId,
                        "decode",
                        "WAV decoding was requested but no voice decoder was configured.",
                        nameof(InvalidOperationException)));
                }
                else
                {
                    try
                    {
                        await using (var input = await lease.OpenOriginalReadAsync(cancellationToken).ConfigureAwait(false))
                        await using (var output = await lease.OpenDecodedWriteAsync(cancellationToken).ConfigureAwait(false))
                        {
                            await _voiceDecoder.DecodeAsync(input, output, cancellationToken).ConfigureAwait(false);
                            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                        }
                        var decodedArtifact = await lease.CommitDecodedAsync(cancellationToken).ConfigureAwait(false);
                        decodedPath = decodedArtifact.RelativePath;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        failures.Enqueue(CreateFailure(record.MessageId, "decode", exception));
                    }
                }
            }

            entries.Enqueue(new VoiceExportEntry(
                record.MessageId,
                record.ConversationId,
                record.OccurredAtUtc,
                record.Direction,
                originalArtifact.RelativePath,
                originalArtifact.ByteLength,
                originalArtifact.Sha256,
                decodedPath));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            failures.Enqueue(CreateFailure(record.MessageId, "export", exception));
        }
        finally
        {
            if (lease is not null)
            {
                try
                {
                    await lease.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failures.Enqueue(CreateFailure(record.MessageId, "rollback", exception));
                }

                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<ExportArtifact> CopyOriginalAsync(
        VoiceRecord record,
        IExportItemLease lease,
        CancellationToken cancellationToken)
    {
        await using var input = await _voiceCatalog.OpenPayloadAsync(record.PayloadLocator, cancellationToken).ConfigureAwait(false);
        if (!input.CanRead)
        {
            throw new InvalidOperationException("The voice catalog returned a non-readable payload stream.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            await using (var output = await lease.OpenOriginalWriteAsync(cancellationToken).ConfigureAwait(false))
            {
                while (true)
                {
                    var count = await input.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken).ConfigureAwait(false);
                    if (count == 0)
                    {
                        break;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            return await lease.CommitOriginalAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static VoiceExportFailure CreateFailure(string? messageId, string stage, Exception exception)
        => new(messageId, stage, string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message, exception.GetType().FullName);

    private sealed class LegacyVoiceCatalog : IVoiceCatalog
    {
        private readonly IVoiceSource _source;
        private readonly ConcurrentDictionary<string, VoiceMessage> _messagesByLocator = new(StringComparer.Ordinal);

        public LegacyVoiceCatalog(IVoiceSource source) => _source = source;

        public async IAsyncEnumerable<ContactRecord> QueryContactsAsync(
            ContactQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<VoiceRecord> QueryVoicesAsync(
            VoiceQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var message in _source.QueryAsync(query, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var locator = message.PayloadReference ?? message.MessageId;
                _messagesByLocator[locator] = message;
                yield return new VoiceRecord(
                    message.MessageId,
                    message.ConversationId,
                    message.OccurredAtUtc,
                    message.Direction,
                    new VoicePayloadLocator("legacy", null, locator));
            }
        }

        public ValueTask<Stream> OpenPayloadAsync(VoicePayloadLocator locator, CancellationToken cancellationToken)
        {
            if (!_messagesByLocator.TryGetValue(locator.BlobKey, out var message))
            {
                throw new KeyNotFoundException($"The legacy payload locator was not associated with a queried voice message: '{locator.BlobKey}'.");
            }
            return _source.OpenPayloadAsync(message, cancellationToken);
        }
    }
}
