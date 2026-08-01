using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace WeChatVoice.KeyBroker;

internal static class BrokerClientIdentityVerifier
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;

    internal static string? Verify(SafePipeHandle pipe)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Pipe client identity verification is Windows-only.");
        }

        // Unit tests and development invocations run unelevated. The shipped
        // requireAdministrator Broker always takes this branch, so production
        // requests retain mandatory PID/SID binding without making the test
        // transport pretend to be an elevated security boundary.
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            return WindowsIdentity.GetCurrent().User?.Value;
        }

        if (!GetNamedPipeClientProcessId(pipe.DangerousGetHandle(), out var clientPid)
            || clientPid == 0
            || clientPid == Environment.ProcessId)
        {
            throw new UnauthorizedAccessException("The broker could not bind the pipe to a distinct client process.");
        }

        using var process = Process.GetProcessById(checked((int)clientPid));
        using var processHandle = new SafeProcessHandle(OpenProcess(ProcessQueryLimitedInformation, false, clientPid), ownsHandle: true);
        if (processHandle.IsInvalid || !OpenProcessToken(processHandle, TokenQuery, out var tokenHandle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The broker could not query the pipe client token.");
        }

        using (tokenHandle)
        {
            var clientSid = ReadUserSid(tokenHandle);
            var serverSid = WindowsIdentity.GetCurrent().User?.Value;
            if (string.IsNullOrWhiteSpace(serverSid) || !string.Equals(clientSid, serverSid, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("The pipe client is not running as the same user as the elevated broker request.");
            }

            return clientSid;
        }
    }

    private static string ReadUserSid(SafeAccessTokenHandle token)
    {
        GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out var required);
        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!GetTokenInformation(token, TokenUser, buffer, required, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The broker could not read the pipe client SID.");
            }

            var sid = Marshal.ReadIntPtr(buffer);
            if (!ConvertSidToStringSid(sid, out var sidText))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The broker could not convert the pipe client SID.");
            }

            try
            {
                return Marshal.PtrToStringUni(sidText) ?? throw new InvalidDataException("The pipe client SID was empty.");
            }
            finally
            {
                LocalFree(sidText);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeHandle processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeHandle tokenHandle, int tokenInformationClass, IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
