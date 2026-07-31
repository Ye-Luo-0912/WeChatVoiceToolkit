using System.Runtime.InteropServices;

namespace WeChatVoice.Windows;

[Flags]
internal enum ProcessAccessRights : uint
{
    // The elevated diagnostic helper uses only QueryLimitedInformation. VmRead
    // is reachable solely by the one-shot Key Broker through an internal API.
    VmRead = 0x0010,
    QueryLimitedInformation = 0x1000,
}

[StructLayout(LayoutKind.Sequential)]
internal struct MemoryBasicInformation
{
    internal nint BaseAddress;
    internal nint AllocationBase;
    internal uint AllocationProtect;
    internal nuint RegionSize;
    internal uint State;
    internal uint Protect;
    internal uint Type;
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nuint VirtualQueryEx(
        SafeProcessHandle process,
        nint address,
        out MemoryBasicInformation buffer,
        nuint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReadProcessMemory(
        SafeProcessHandle process,
        nint baseAddress,
        [Out] byte[] buffer,
        nuint size,
        out nuint bytesRead);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(SafeProcessHandle processHandle, uint desiredAccess, out nint tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetTokenInformation(
        nint tokenHandle,
        int tokenInformationClass,
        nint tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWow64Process2(SafeProcessHandle process, out ushort processMachine, out ushort nativeMachine);

    [LibraryImport("wintrust.dll", EntryPoint = "WinVerifyTrust")]
    internal static partial int WinVerifyTrust(nint windowHandle, ref Guid actionId, ref WinTrustData data);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WinTrustFileInfo
{
    internal uint Size;
    internal nint FilePath;
    internal nint FileHandle;
    internal nint KnownSubject;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WinTrustData
{
    internal uint Size;
    internal nint PolicyCallbackData;
    internal nint SipClientData;
    internal uint UiChoice;
    internal uint RevocationChecks;
    internal uint UnionChoice;
    internal nint FileInfo;
    internal uint StateAction;
    internal nint StateData;
    internal nint UrlReference;
    internal uint ProviderFlags;
    internal uint UiContext;
    internal nint SignatureSettings;
}
