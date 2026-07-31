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
    internal const uint ToolhelpSnapshotProcess = 0x00000002;
    internal static readonly nint InvalidHandleValue = new nint(-1);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(
        ProcessAccessRights desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32First(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32Next(nint snapshot, ref ProcessEntry32 entry);

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
internal struct ProcessEntry32
{
    internal ProcessEntry32(uint size)
    {
        Size = size;
        Usage = 0;
        ProcessId = 0;
        HeapId = 0;
        ModuleId = 0;
        Threads = 0;
        ParentProcessId = 0;
        Priority = 0;
        Flags = 0;
        ExeFile = string.Empty;
    }

    internal uint Size;
    internal uint Usage;
    internal uint ProcessId;
    internal nuint HeapId;
    internal uint ModuleId;
    internal uint Threads;
    internal uint ParentProcessId;
    internal int Priority;
    internal uint Flags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    internal string ExeFile;
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
