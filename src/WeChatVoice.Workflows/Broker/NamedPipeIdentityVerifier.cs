using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WeChatVoice.Workflows.Broker;

internal static partial class NamedPipeIdentityVerifier
{
    internal static void VerifyServerProcess(SafePipeHandle pipeHandle, int expectedProcessId)
    {
        ArgumentNullException.ThrowIfNull(pipeHandle);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Named pipe process identity verification is Windows-only.");
        }

        if (expectedProcessId <= 0 || !GetNamedPipeServerProcessId(pipeHandle, out var actualProcessId))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The Key Broker pipe server process identity could not be verified.");
        }

        if (actualProcessId != (uint)expectedProcessId)
        {
            throw new UnauthorizedAccessException("The connected named pipe server was not the Broker process that was started for this request.");
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
