using System.Diagnostics;
using System.Text.Json;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Infrastructure.Materialization;

/// <summary>
/// Persists materialization commit markers with a monotonic state machine.
/// Every marker is bound to its source Snapshot, backend, manifest bytes, and
/// Workspace identity once the manifest exists.
/// </summary>
public static class MaterializationStateStore
{
    public const string RelativeStatePath = ".wechatvoice/materialization-state.json";
    public const string RelativeLockPath = ".wechatvoice/materialization.lock";
    public const string RelativeDurationCachePath = ".wechatvoice/duration-cache.jsonl";
    public const string RelativeDeepScanCachePath = ".wechatvoice/deep-scan-cache.jsonl";

    private static readonly string[] FailureAllowedStates =
    [
        MaterializationCommitStates.Staging,
        MaterializationCommitStates.DatabasesCommitted,
        MaterializationCommitStates.WorkspaceCommitted,
        MaterializationCommitStates.FailedRecoverable,
    ];

    public static string GetPath(string outputRoot)
        => Path.Combine(Path.GetFullPath(outputRoot), RelativeStatePath.Replace('/', Path.DirectorySeparatorChar));

    public static string GetLockPath(string outputRoot)
        => Path.Combine(Path.GetFullPath(outputRoot), RelativeLockPath.Replace('/', Path.DirectorySeparatorChar));

    public static bool IsStatePath(string? relativePath)
        => string.Equals(relativePath?.Replace('\\', '/'), RelativeStatePath, StringComparison.OrdinalIgnoreCase);

    public static bool IsLockPath(string? relativePath)
        => string.Equals(relativePath?.Replace('\\', '/'), RelativeLockPath, StringComparison.OrdinalIgnoreCase);

    public static bool IsDurationCachePath(string? relativePath)
        => IsCachePath(relativePath, RelativeDurationCachePath);

    public static bool IsDeepScanCachePath(string? relativePath)
        => IsCachePath(relativePath, RelativeDeepScanCachePath);

    public static Task<MaterializationStateDocument> CreateStagingStateAsync(
        string outputRoot,
        string operationId,
        MaterializationStateBinding binding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return TransitionCoreAsync(
            outputRoot,
            Array.Empty<string>(),
            MaterializationCommitStates.Staging,
            operationId,
            failureCode: null,
            cancellationToken,
            binding,
            heldLock: null,
            allowCreateStaging: true);
    }

    public static Task<MaterializationStateDocument> TransitionAsync(
        string outputRoot,
        IReadOnlyCollection<string> expectedStates,
        string nextState,
        string? operationId,
        string? failureCode,
        CancellationToken cancellationToken,
        MaterializationStateBinding? binding = null)
        => TransitionCoreAsync(outputRoot, expectedStates, nextState, operationId, failureCode, cancellationToken, binding, heldLock: null, allowCreateStaging: false);

    public static async Task<bool> TryTransitionToFailedRecoverableAsync(
        string outputRoot,
        string? operationId,
        string? failureCode,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await TransitionAsync(
                outputRoot,
                FailureAllowedStates,
                MaterializationCommitStates.FailedRecoverable,
                operationId,
                failureCode,
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (MaterializationStateTransitionException)
        {
            return false;
        }
    }

    public static async Task<MaterializationStateBinding> ReadManifestBindingAsync(
        string outputRoot,
        CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(outputRoot);
        var manifestPath = Path.Combine(fullRoot, ".wechatvoice", "materialization-manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException("The materialization manifest is missing.");
        }

        var hash = await FileHashing.ComputeSha256Async(manifestPath, cancellationToken).ConfigureAwait(false);
        await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var manifest = await JsonSerializer.DeserializeAsync<MaterializationManifest>(stream, InfrastructureJson.Compact, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The materialization manifest is empty.");
        return new MaterializationStateBinding(manifest.SourceSnapshotId, manifest.BackendId, hash, manifest.WorkspaceId);
    }

    public static async Task<MaterializationStateLock> AcquireLockAsync(
        string outputRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        var lockPath = GetLockPath(outputRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 5.0);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream? stream = null;
            try
            {
                stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete,
                    1,
                    FileOptions.Asynchronous);
                if (stream.Length == 0)
                {
                    stream.SetLength(1);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                if (OperatingSystem.IsMacOS())
                {
                    throw new PlatformNotSupportedException("Materialization state locking requires a platform with byte-range file locks.");
                }

                stream.Lock(0, 1);
                return new MaterializationStateLock(stream);
            }
            catch (IOException exception)
            {
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }

                if (Stopwatch.GetTimestamp() >= deadline)
                {
                    throw new MaterializationStateTransitionException("The materialization state is busy in another process.", exception);
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }
        }
    }

    public static async Task<MaterializationStateDocument> ReadAsync(
        string outputRoot,
        CancellationToken cancellationToken)
    {
        var path = GetPath(outputRoot);
        if (!File.Exists(path))
        {
            throw new InvalidDataException("The materialization commit state is missing.");
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var document = await ReadCoreAsync(stream, cancellationToken).ConfigureAwait(false);
        await ValidateBindingAgainstManifestAsync(outputRoot, document.Binding!, cancellationToken).ConfigureAwait(false);
        return document;
    }

    private static async Task<MaterializationStateDocument> TransitionCoreAsync(
        string outputRoot,
        IReadOnlyCollection<string> expectedStates,
        string nextState,
        string? operationId,
        string? failureCode,
        CancellationToken cancellationToken,
        MaterializationStateBinding? requestedBinding,
        MaterializationStateLock? heldLock,
        bool allowCreateStaging)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentNullException.ThrowIfNull(expectedStates);
        if (!MaterializationCommitStates.IsKnown(nextState))
        {
            throw new ArgumentException($"Unknown materialization commit state '{nextState}'.", nameof(nextState));
        }

        var ownsLock = heldLock is null;
        var stateLock = heldLock ?? await AcquireLockAsync(outputRoot, cancellationToken).ConfigureAwait(false);
        try
        {
            var statePath = GetPath(outputRoot);
            MaterializationStateDocument? current = null;
            if (File.Exists(statePath))
            {
                await using var stream = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                current = await ReadCoreAsync(stream, cancellationToken).ConfigureAwait(false);
            }

            if (current is null)
            {
                if (!allowCreateStaging
                    || expectedStates.Count != 0
                    || !string.Equals(nextState, MaterializationCommitStates.Staging, StringComparison.Ordinal)
                    || requestedBinding is null)
                {
                    throw new MaterializationStateTransitionException("A missing materialization state can only be created as a bound Staging state.");
                }
            }
            else
            {
                if (!expectedStates.Contains(current.State, StringComparer.Ordinal))
                {
                    throw new MaterializationStateTransitionException($"The materialization state '{current.State}' was not an expected predecessor of '{nextState}'.");
                }

                requestedBinding = MergeBinding(
                    current.Binding ?? throw new MaterializationStateTransitionException("The materialization state lacks its binding."),
                    requestedBinding);
                if (string.Equals(current.State, nextState, StringComparison.Ordinal))
                {
                    await ValidateBindingAgainstManifestAsync(outputRoot, requestedBinding, cancellationToken).ConfigureAwait(false);
                    return current;
                }

                if (!CanTransition(current.State, nextState))
                {
                    throw new MaterializationStateTransitionException($"The materialization state cannot transition from '{current.State}' to '{nextState}'.");
                }
            }

            requestedBinding ??= current?.Binding;
            if (requestedBinding is null)
            {
                throw new MaterializationStateTransitionException("Every materialization state must be bound to its source Snapshot and backend.");
            }

            if (nextState is not (MaterializationCommitStates.Staging or MaterializationCommitStates.FailedRecoverable)
                && !requestedBinding.IsComplete)
            {
                throw new MaterializationStateTransitionException("Committed materialization states require a manifest SHA-256 and WorkspaceId binding.");
            }

            await ValidateBindingAgainstManifestAsync(outputRoot, requestedBinding, cancellationToken).ConfigureAwait(false);
            var document = new MaterializationStateDocument(
                nextState,
                DateTimeOffset.UtcNow,
                failureCode,
                string.IsNullOrWhiteSpace(operationId) ? Guid.NewGuid().ToString("N") : operationId,
                requestedBinding);
            await AtomicFileWriter.WriteJsonAsync(statePath, document, InfrastructureJson.Compact, cancellationToken).ConfigureAwait(false);
            return document;
        }
        finally
        {
            if (ownsLock)
            {
                await stateLock.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public static Task<MaterializationStateDocument> TransitionAsync(
        string outputRoot,
        IReadOnlyCollection<string> expectedStates,
        string nextState,
        string? operationId,
        string? failureCode,
        CancellationToken cancellationToken,
        MaterializationStateLock heldLock,
        MaterializationStateBinding? binding = null)
        => TransitionCoreAsync(outputRoot, expectedStates, nextState, operationId, failureCode, cancellationToken, binding, heldLock, allowCreateStaging: false);

    public static async Task<bool> TryTransitionToFailedRecoverableAsync(
        string outputRoot,
        string? operationId,
        string? failureCode,
        CancellationToken cancellationToken,
        MaterializationStateLock heldLock)
    {
        try
        {
            await TransitionAsync(
                outputRoot,
                FailureAllowedStates,
                MaterializationCommitStates.FailedRecoverable,
                operationId,
                failureCode,
                cancellationToken,
                heldLock).ConfigureAwait(false);
            return true;
        }
        catch (MaterializationStateTransitionException)
        {
            return false;
        }
    }

    private static bool CanTransition(string currentState, string nextState)
        => nextState == MaterializationCommitStates.FailedRecoverable
            ? currentState != MaterializationCommitStates.Completed
            : (currentState, nextState) switch
            {
                (MaterializationCommitStates.Staging, MaterializationCommitStates.DatabasesCommitted) => true,
                (MaterializationCommitStates.DatabasesCommitted, MaterializationCommitStates.WorkspaceCommitted) => true,
                (MaterializationCommitStates.FailedRecoverable, MaterializationCommitStates.WorkspaceCommitted) => true,
                (MaterializationCommitStates.WorkspaceCommitted, MaterializationCommitStates.Completed) => true,
                _ => false,
            };

    private static MaterializationStateBinding MergeBinding(
        MaterializationStateBinding current,
        MaterializationStateBinding? requested)
    {
        if (requested is null)
        {
            return current;
        }

        if (!string.Equals(current.SourceSnapshotId, requested.SourceSnapshotId, StringComparison.Ordinal)
            || !string.Equals(current.BackendId, requested.BackendId, StringComparison.Ordinal)
            || current.ManifestSha256 is not null
                && !string.Equals(current.ManifestSha256, requested.ManifestSha256, StringComparison.OrdinalIgnoreCase)
            || current.WorkspaceId is not null
                && !string.Equals(current.WorkspaceId, requested.WorkspaceId, StringComparison.Ordinal))
        {
            throw new MaterializationStateTransitionException("The materialization state binding changed between transitions.");
        }

        return new MaterializationStateBinding(
            current.SourceSnapshotId,
            current.BackendId,
            requested.ManifestSha256 ?? current.ManifestSha256,
            requested.WorkspaceId ?? current.WorkspaceId);
    }

    private static async Task ValidateBindingAgainstManifestAsync(
        string outputRoot,
        MaterializationStateBinding binding,
        CancellationToken cancellationToken)
    {
        if (binding.ManifestSha256 is null && binding.WorkspaceId is null)
        {
            return;
        }

        var manifestPath = Path.Combine(Path.GetFullPath(outputRoot), ".wechatvoice", "materialization-manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new MaterializationStateTransitionException("The materialization manifest is required by the state binding.");
        }

        var actualHash = await FileHashing.ComputeSha256Async(manifestPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualHash, binding.ManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new MaterializationStateTransitionException("The materialization state is bound to a different manifest.");
        }

        await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var manifest = await JsonSerializer.DeserializeAsync<MaterializationManifest>(stream, InfrastructureJson.Compact, cancellationToken).ConfigureAwait(false)
            ?? throw new MaterializationStateTransitionException("The bound materialization manifest is empty.");
        if (!string.Equals(manifest.WorkspaceId, binding.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(manifest.SourceSnapshotId, binding.SourceSnapshotId, StringComparison.Ordinal)
            || !string.Equals(manifest.BackendId, binding.BackendId, StringComparison.Ordinal))
        {
            throw new MaterializationStateTransitionException("The materialization state binding does not match its manifest.");
        }
    }

    private static async Task<MaterializationStateDocument> ReadCoreAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var document = await JsonSerializer.DeserializeAsync<MaterializationStateDocument>(stream, InfrastructureJson.Compact, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The materialization commit state is empty.");
        if (!MaterializationCommitStates.IsKnown(document.State))
        {
            throw new InvalidDataException("The materialization commit state is unknown.");
        }

        if (document.Binding is null)
        {
            throw new InvalidDataException("The materialization commit state lacks its source/manifest binding.");
        }

        return document;
    }

    private static bool IsCachePath(string? relativePath, string cachePath)
    {
        var normalized = relativePath?.Replace('\\', '/');
        return string.Equals(normalized, cachePath, StringComparison.OrdinalIgnoreCase)
            || normalized?.StartsWith(cachePath + ".", StringComparison.OrdinalIgnoreCase) == true;
    }
}

public sealed class MaterializationStateLock : IAsyncDisposable
{
    private readonly FileStream _stream;
    private int _disposed;

    internal MaterializationStateLock(FileStream stream) => _stream = stream;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (!OperatingSystem.IsMacOS())
            {
                _stream.Unlock(0, 1);
            }
        }
        catch (IOException)
        {
        }

        await _stream.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class MaterializationStateTransitionException : IOException
{
    public MaterializationStateTransitionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
