using System.Diagnostics;
using System.Text.Json;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Infrastructure.Materialization;

/// <summary>
/// Persists materialization commit markers with a monotonic state machine. The
/// lock file is an advisory cross-process lease held for the complete recovery
/// or deletion operation, while each transition also takes the same lock for
/// its read/validate/write sequence.
/// </summary>
public static class MaterializationStateStore
{
    public const string RelativeStatePath = ".wechatvoice/materialization-state.json";
    public const string RelativeLockPath = ".wechatvoice/materialization.lock";

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

    /// <summary>
    /// Compatibility helper for tests and migration tooling that creates the
    /// initial marker in a new output root. Production code must use
    /// <see cref="TransitionAsync"/> for every subsequent state change.
    /// </summary>
    public static Task WriteAsync(
        string outputRoot,
        string state,
        CancellationToken cancellationToken,
        string? failureCode = null)
        => TransitionAsync(
            outputRoot,
            Array.Empty<string>(),
            state,
            operationId: "legacy-" + Guid.NewGuid().ToString("N"),
            failureCode,
            cancellationToken);

    public static Task<MaterializationStateDocument> TransitionAsync(
        string outputRoot,
        IReadOnlyCollection<string> expectedStates,
        string nextState,
        string? operationId,
        string? failureCode,
        CancellationToken cancellationToken)
        => TransitionCoreAsync(outputRoot, expectedStates, nextState, operationId, failureCode, cancellationToken, heldLock: null);

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
            // A completed materialization is terminal and must never be
            // downgraded merely because a later response/cleanup failed.
            return false;
        }
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
                    throw new MaterializationStateTransitionException(
                        "The materialization state is busy in another process.",
                        exception);
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
        return await ReadCoreAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<MaterializationStateDocument> TransitionCoreAsync(
        string outputRoot,
        IReadOnlyCollection<string> expectedStates,
        string nextState,
        string? operationId,
        string? failureCode,
        CancellationToken cancellationToken,
        MaterializationStateLock? heldLock)
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

            if (current is not null && string.Equals(current.State, nextState, StringComparison.Ordinal))
            {
                return current;
            }

            if (current is null)
            {
                if (expectedStates.Count != 0)
                {
                    throw new MaterializationStateTransitionException("The materialization state is missing for the requested transition.");
                }
            }
            else
            {
                if (!expectedStates.Contains(current.State, StringComparer.Ordinal))
                {
                    throw new MaterializationStateTransitionException($"The materialization state '{current.State}' was not an expected predecessor of '{nextState}'.");
                }

                if (!CanTransition(current.State, nextState))
                {
                    throw new MaterializationStateTransitionException($"The materialization state cannot transition from '{current.State}' to '{nextState}'.");
                }
            }

            var document = new MaterializationStateDocument(
                nextState,
                DateTimeOffset.UtcNow,
                failureCode,
                string.IsNullOrWhiteSpace(operationId) ? Guid.NewGuid().ToString("N") : operationId);
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
        MaterializationStateLock heldLock)
        => TransitionCoreAsync(outputRoot, expectedStates, nextState, operationId, failureCode, cancellationToken, heldLock);

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

        return document;
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
