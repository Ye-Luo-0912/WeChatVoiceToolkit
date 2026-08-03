using WeChatVoice.Core.Models;

namespace WeChatVoice.Core.Ports;

/// <summary>
/// Test-only seam for exercising crash recovery. Implementations may throw at
/// a selected checkpoint; callers must never use this to alter production
/// export behavior.
/// </summary>
public interface IExportTransactionFaultInjector
{
    void ThrowIfRequested(
        ExportTransactionFaultPoint point,
        string runId,
        string? messageId);
}
