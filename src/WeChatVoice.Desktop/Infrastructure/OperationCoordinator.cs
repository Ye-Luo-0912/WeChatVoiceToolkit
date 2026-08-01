namespace WeChatVoice.Desktop.Infrastructure;

/// <summary>
/// Serializes high-cost Desktop workflows across all pages. The coordinator
/// deliberately exposes a non-blocking acquisition so a second click cannot
/// queue behind the active operation and appear to be running.
/// </summary>
public sealed class OperationCoordinator : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _busy;

    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    public bool TryAcquire(out IDisposable lease)
    {
        if (!_gate.Wait(0))
        {
            lease = NoopLease.Instance;
            return false;
        }

        Interlocked.Exchange(ref _busy, 1);
        OnPropertyChanged(nameof(IsBusy));
        lease = new Lease(this, _gate);
        return true;
    }

    private sealed class Lease : IDisposable
    {
        private readonly SemaphoreSlim _gate;
        private readonly OperationCoordinator _owner;
        private int _disposed;

        public Lease(OperationCoordinator owner, SemaphoreSlim gate) { _owner = owner; _gate = gate; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _gate.Release();
                Interlocked.Exchange(ref _owner._busy, 0);
                _owner.OnPropertyChanged(nameof(IsBusy));
            }
        }
    }

    private sealed class NoopLease : IDisposable
    {
        public static NoopLease Instance { get; } = new();
        public void Dispose() { }
    }
}
