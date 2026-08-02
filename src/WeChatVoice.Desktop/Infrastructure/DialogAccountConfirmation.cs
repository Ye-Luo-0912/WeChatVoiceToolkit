using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;

namespace WeChatVoice.Desktop.Infrastructure;

/// <summary>One interactive account-confirmation request.</summary>
public sealed class PendingAccountConfirmation
{
    internal PendingAccountConfirmation(AccountIdentityReport report)
    {
        RequestId = Guid.NewGuid();
        Report = report;
        Completion = new TaskCompletionSource<AccountConfirmation>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Guid RequestId { get; }

    public AccountIdentityReport Report { get; }

    internal TaskCompletionSource<AccountConfirmation> Completion { get; }

    internal CancellationTokenRegistration CancellationRegistration { get; set; }

    internal void DisposeCancellationRegistration() => CancellationRegistration.Dispose();
}

/// <summary>
/// UI-backed account confirmation. Every ConfirmAsync call creates a fresh
/// pending request, and a second concurrent request is rejected. The caller
/// supplies the UI dispatcher so the request event is never raised on the
/// workflow worker thread.
/// </summary>
public sealed class DialogAccountConfirmation : IAccountConfirmation
{
    private readonly object _gate = new();
    private readonly Func<Action, Task> _marshal;
    private PendingAccountConfirmation? _pending;

    public DialogAccountConfirmation(Func<Action, Task>? invokeOnUi = null)
        => _marshal = invokeOnUi ?? (action =>
        {
            action();
            return Task.CompletedTask;
        });

    public event EventHandler<AccountIdentityReport>? ConfirmationRequested;

    public PendingAccountConfirmation? Pending
    {
        get
        {
            lock (_gate)
            {
                return _pending;
            }
        }
    }

    public async Task<AccountConfirmation> ConfirmAsync(
        AccountIdentityReport report,
        CancellationToken cancellationToken)
    {
        var pending = new PendingAccountConfirmation(report);
        lock (_gate)
        {
            if (_pending is not null)
            {
                throw new InvalidOperationException("An account confirmation request is already pending.");
            }

            _pending = pending;
        }

        pending.CancellationRegistration = cancellationToken.Register(
            static state =>
            {
                var request = (PendingCancellationState)state!;
                request.Owner.Cancel(request.Pending, request.Token);
            },
            new PendingCancellationState(this, pending, cancellationToken));

        try
        {
            var shouldRaise = false;
            lock (_gate)
            {
                shouldRaise = ReferenceEquals(_pending, pending);
            }

            if (shouldRaise)
            {
                await _marshal(() =>
                {
                    lock (_gate)
                    {
                        if (!ReferenceEquals(_pending, pending))
                        {
                            return;
                        }
                    }

                    ConfirmationRequested?.Invoke(this, report);
                }).ConfigureAwait(false);
            }

            return await pending.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            ClearIfCurrent(pending);
            pending.DisposeCancellationRegistration();
        }
    }

    /// <summary>Completes the currently displayed request.</summary>
    public bool Complete(bool confirmed, string? confirmedAccountId)
    {
        PendingAccountConfirmation? pending;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
        }

        if (pending is null)
        {
            return false;
        }

        pending.Completion.TrySetResult(new AccountConfirmation(confirmed, confirmedAccountId));
        pending.DisposeCancellationRegistration();
        return true;
    }

    private void Cancel(PendingAccountConfirmation pending, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_pending, pending))
            {
                return;
            }

            _pending = null;
        }

        pending.Completion.TrySetCanceled(cancellationToken);
    }

    private void ClearIfCurrent(PendingAccountConfirmation pending)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_pending, pending))
            {
                _pending = null;
            }
        }
    }

    private sealed record PendingCancellationState(
        DialogAccountConfirmation Owner,
        PendingAccountConfirmation Pending,
        CancellationToken Token);
}
