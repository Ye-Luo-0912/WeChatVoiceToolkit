using System.Runtime.InteropServices;

namespace WeChatVoice.Windows;

[Flags]
internal enum ProcessAccessRights : uint
{
    // This is intentionally the only access right represented by this project.
    // It permits limited metadata queries and does not permit process-memory access.
    QueryLimitedInformation = 0x1000,
}

internal static partial class NativeMethods
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(
        ProcessAccessRights desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);
}
