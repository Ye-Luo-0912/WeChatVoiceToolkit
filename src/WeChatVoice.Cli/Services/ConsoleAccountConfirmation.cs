using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;

namespace WeChatVoice.Cli.Services;

/// <summary>
/// Console implementation of the account confirmation boundary. A future UI
/// host implements <see cref="IAccountConfirmation"/> with a dialog instead;
/// the stable report/confirmation shape does not change.
/// </summary>
internal sealed class ConsoleAccountConfirmation : IAccountConfirmation
{
    public Task<AccountConfirmation> ConfirmAsync(AccountIdentityReport report, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Console.Error.WriteLine($"检测到账号：{report.AccountCandidate}，请确认这是您要处理的微信账号（y/N）：");
        var line = Console.ReadLine();
        var confirmed = string.Equals(line?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(new AccountConfirmation(confirmed, confirmed ? report.AccountCandidate : null));
    }
}
