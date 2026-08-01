using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;

namespace WeChatVoice.Desktop.Infrastructure;

/// <summary>
/// UI-backed account confirmation port. The workflow blocks on
/// <see cref="ConfirmAsync"/> while the page VM raises the confirmation dialog
/// on the UI thread and completes this port with the user's answer. Declining
/// returns <c>Confirmed = false</c>; the workflow then fails with
/// <see cref="Core.Errors.ErrorCode.AccountConfirmationRequired"/>.
/// </summary>
public sealed class DialogAccountConfirmation : IAccountConfirmation
{
    private readonly TaskCompletionSource<AccountConfirmation> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event EventHandler<AccountIdentityReport>? ConfirmationRequested;

    public async Task<AccountConfirmation> ConfirmAsync(
        AccountIdentityReport report,
        CancellationToken cancellationToken)
    {
        ConfirmationRequested?.Invoke(this, report);
        return await _tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Complete(bool confirmed, string? confirmedAccountId)
        => _tcs.TrySetResult(new AccountConfirmation(confirmed, confirmedAccountId));
}
