using Microsoft.Win32.SafeHandles;

namespace WeChatVoice.Windows;

/// <summary>
/// Owns a native process handle and guarantees that it is closed exactly once.
/// It is internal so consumers cannot turn this library into a general process API.
/// </summary>
internal sealed class SafeProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeProcessHandle()
        : base(ownsHandle: true)
    {
    }

    internal static SafeProcessHandle FromNativeHandle(nint nativeHandle)
    {
        var processHandle = new SafeProcessHandle();
        processHandle.SetHandle(nativeHandle);
        return processHandle;
    }

    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}
