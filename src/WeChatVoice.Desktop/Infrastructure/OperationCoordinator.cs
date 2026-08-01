namespace WeChatVoice.Desktop.Infrastructure;

/// <summary>
/// Serializes high-cost Desktop workflows across all pages. The coordinator
/// deliberately exposes a non-blocking acquisition so a second click cannot
/// queue behind the active operation and appear to be running.
/// </summary>
public sealed class OperationCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool TryAcquire(out IDisposable lease)
    {
        if (!_gate.Wait(0))
        {
            lease = NoopLease.Instance;
            return false;
        }

        lease = new Lease(_gate);
        return true;
    }

    private sealed class Lease : IDisposable
    {
        private readonly SemaphoreSlim _gate;
        private int _disposed;

        public Lease(SemaphoreSlim gate) => _gate = gate;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _gate.Release();
            }
        }
    }

    private sealed class NoopLease : IDisposable
    {
        public static NoopLease Instance { get; } = new();
        public void Dispose() { }
    }
}
