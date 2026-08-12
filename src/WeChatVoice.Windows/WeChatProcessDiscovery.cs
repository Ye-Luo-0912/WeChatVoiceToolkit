using System.ComponentModel;
using System.Diagnostics;

namespace WeChatVoice.Windows;

/// <summary>
/// Finds only the known WeChat desktop process names. This type intentionally
/// returns metadata only; it does not expose a process handle or process memory.
/// </summary>
public static class WeChatProcessDiscovery
{
    private static readonly string[] KnownProcessNames = ["WeChat", "WeChatAppEx", "Weixin"];

    /// <summary>
    /// Gets the process names that this toolkit deliberately recognizes.
    /// </summary>
    public static IReadOnlyList<string> SupportedProcessNames { get; } = Array.AsReadOnly(KnownProcessNames);

    /// <summary>
    /// Lists currently-running, recognized WeChat processes.
    /// </summary>
    public static IReadOnlyList<WeChatProcessInfo> ListRunning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<WeChatProcessInfo>();
        }

        var results = new List<WeChatProcessInfo>();
        var seenProcessIds = new HashSet<int>();

        foreach (var processName in KnownProcessNames)
        {
            Process[] processes;
            try
            {
                // Query only fixed, application-specific names. Do not accept a caller-provided name.
                processes = Process.GetProcessesByName(processName);
            }
            catch (InvalidOperationException)
            {
                continue;
            }
            catch (Win32Exception)
            {
                continue;
            }

            foreach (var process in processes)
            {
                using (process)
                {
                    try
                    {
                        var processId = process.Id;
                        var actualName = process.ProcessName;

                        if (processId > 0 && IsKnownProcessName(actualName) && seenProcessIds.Add(processId))
                        {
                            string? productVersion = null;
                            try
                            {
                                productVersion = process.MainModule?.FileVersionInfo.ProductVersion;
                            }
                            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
                            {
                                // Version is diagnostic metadata only. The
                                // identity verifier remains authoritative.
                            }

                            results.Add(new WeChatProcessInfo(processId, actualName, productVersion));
                        }
                    }
                    // A process can exit, or access can be denied, between enumeration and inspection.
                    catch (InvalidOperationException)
                    {
                    }
                    catch (Win32Exception)
                    {
                    }
                }
            }
        }

        results.Sort(static (left, right) => left.ProcessId.CompareTo(right.ProcessId));
        return results;
    }

    internal static bool IsKnownProcessName(string? processName) =>
        processName is not null && KnownProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase);
}
