using WeChatVoice.Core.Models;

namespace WeChatVoice.Core.Ports;

/// <summary>
/// Account identity confirmation boundary. The CLI implements it with a
/// console prompt; a future UI host implements it with a dialog. The caller
/// must never silently pick a candidate account.
/// </summary>
public interface IAccountConfirmation
{
    Task<AccountConfirmation> ConfirmAsync(AccountIdentityReport report, CancellationToken cancellationToken);
}
