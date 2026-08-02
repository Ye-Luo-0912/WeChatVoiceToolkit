using System.Runtime.InteropServices;

namespace WeChatVoice.Infrastructure.Export;

internal static class ArtifactFileIdentity
{
    public static string Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!OperatingSystem.IsWindows())
        {
            var fileInfo = new FileInfo(path);
            return $"fallback:{fileInfo.CreationTimeUtc.Ticks}:{fileInfo.Length}";
        }
        if (!GetFileInformationByHandle(stream.SafeFileHandle.DangerousGetHandle(), out var handleInfo))
            throw new IOException("The artifact file identity could not be read.");
        return $"{handleInfo.VolumeSerialNumber:x8}:{handleInfo.FileIndexHigh:x8}{handleInfo.FileIndexLow:x8}";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(IntPtr handle, out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
