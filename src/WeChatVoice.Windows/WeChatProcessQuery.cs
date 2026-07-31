using System.ComponentModel;
using System.Diagnostics;

namespace WeChatVoice.Windows;

/// <summary>
/// Internal bridge for the rare case where limited native process metadata must
/// be queried. It deliberately exposes no read/write-memory interop.
/// </summary>
internal static class WeChatProcessQuery
{
    internal static SafeProcessHandle? TryOpenForLimitedQuery(WeChatProcessInfo process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (!OperatingSystem.IsWindows() || process.ProcessId <= 0 || !WeChatProcessDiscovery.IsKnownProcessName(process.ProcessName))
        {
            return null;
        }

        // Revalidate the process name immediately before opening a handle. The only
        // permitted handle access is query-limited information, so a PID race cannot
        // become a process-memory or command-execution capability.
        try
        {
            using var liveProcess = Process.GetProcessById(process.ProcessId);
            if (!WeChatProcessDiscovery.IsKnownProcessName(liveProcess.ProcessName) ||
                !string.Equals(liveProcess.ProcessName, process.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }

        var nativeHandle = NativeMethods.OpenProcess(
            ProcessAccessRights.QueryLimitedInformation,
            inheritHandle: false,
            checked((uint)process.ProcessId));
        var handle = SafeProcessHandle.FromNativeHandle(nativeHandle);

        if (!handle.IsInvalid)
        {
            return handle;
        }

        handle.Dispose();
        return null;
    }
}
