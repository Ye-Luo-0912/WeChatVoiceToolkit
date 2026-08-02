using System.Runtime.InteropServices;

namespace WeChatVoice.Infrastructure.Sqlite;

internal static class FileIdentity
{
    public static string Read(string path)
    {
        var fullPath = Path.GetFullPath(path);
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Read(stream);
    }

    internal static string Read(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!OperatingSystem.IsWindows())
        {
            var info = new FileInfo(stream.Name);
            return $"fallback:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }

        if (!GetFileInformationByHandle(stream.SafeFileHandle.DangerousGetHandle(), out var value))
            throw new IOException("The database file identity could not be read.");
        return $"{value.VolumeSerialNumber:x8}:{value.FileIndexHigh:x8}{value.FileIndexLow:x8}";
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
