using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace WeChatVoice.Windows;

internal delegate bool ProcessMemoryChunkHandler(ReadOnlySpan<byte> chunk, bool startsRegion);

public readonly record struct ProcessMemoryScanResult(
    int RegionCount,
    long ScannedBytes,
    bool ReachedLimit);

internal sealed record WeixinProcessMemoryIdentity(
    WeChatProcessInfo Process,
    string ImagePath,
    DateTime StartedAtUtc,
    int SessionId);

/// <summary>
/// Broker-only, read-only process-memory session. It exposes chunks but never
/// addresses and applies non-configurable safety ceilings before invoking the
/// caller. The diagnostic ElevatedHelper cannot access this internal type.
/// </summary>
internal sealed class WeixinProcessMemorySession : IDisposable
{
    internal const int ChunkSize = 1024 * 1024;
    internal const long MaximumRegionBytes = 128L * 1024 * 1024;
    internal const long MaximumTotalBytes = 768L * 1024 * 1024;
    internal const int MaximumRegions = 8192;
    internal static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(30);

    private const uint MemCommit = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;
    private const uint ProtectionBaseMask = 0xFF;

    private static readonly HashSet<uint> ReadableProtections =
    [
        0x02, // PAGE_READONLY
        0x04, // PAGE_READWRITE
        0x08, // PAGE_WRITECOPY
        0x20, // PAGE_EXECUTE_READ
        0x40, // PAGE_EXECUTE_READWRITE
        0x80, // PAGE_EXECUTE_WRITECOPY
    ];

    private readonly SafeProcessHandle handle;
    private readonly int processId;
    private bool disposed;

    private WeixinProcessMemorySession(SafeProcessHandle handle, int processId)
    {
        this.handle = handle;
        this.processId = processId;
    }

    internal static WeixinProcessMemorySession? TryOpen(WeixinProcessMemoryIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var process = identity.Process;
        if (!OperatingSystem.IsWindows() || process.ProcessId <= 0 ||
            !string.Equals(process.ProcessName, "Weixin", StringComparison.OrdinalIgnoreCase) ||
            !HasSameLiveIdentity(identity))
        {
            return null;
        }

        var nativeHandle = NativeMethods.OpenProcess(
            ProcessAccessRights.QueryLimitedInformation | ProcessAccessRights.VmRead,
            inheritHandle: false,
            checked((uint)process.ProcessId));
        var safeHandle = SafeProcessHandle.FromNativeHandle(nativeHandle);
        if (safeHandle.IsInvalid || !HasSameLiveIdentity(identity))
        {
            safeHandle.Dispose();
            return null;
        }

        return new WeixinProcessMemorySession(safeHandle, process.ProcessId);
    }

    internal ProcessMemoryScanResult ScanReadableMemory(
        ProcessMemoryChunkHandler handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(disposed, this);

        var started = Stopwatch.StartNew();
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var regionCount = 0;
        long scannedBytes = 0;
        var reachedLimit = false;
        try
        {
            nuint address = 0;
            while (regionCount < MaximumRegions && started.Elapsed < MaximumDuration)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var queried = NativeMethods.VirtualQueryEx(
                    handle,
                    unchecked((nint)address),
                    out var information,
                    checked((nuint)Marshal.SizeOf<MemoryBasicInformation>()));
                if (queried == 0)
                {
                    break;
                }

                var baseAddress = unchecked((nuint)information.BaseAddress);
                var regionSize = information.RegionSize;
                var nextAddress = baseAddress + regionSize;
                if (nextAddress <= address)
                {
                    break;
                }

                address = nextAddress;
                if (!IsReadable(information) || regionSize == 0 || regionSize > (nuint)MaximumRegionBytes)
                {
                    continue;
                }

                regionCount++;
                nuint offset = 0;
                var startsRegion = true;
                while (offset < regionSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (started.Elapsed >= MaximumDuration || scannedBytes >= MaximumTotalBytes)
                    {
                        reachedLimit = true;
                        return new ProcessMemoryScanResult(regionCount, scannedBytes, reachedLimit);
                    }

                    var remainingRegion = regionSize - offset;
                    var remainingBudget = checked((nuint)(MaximumTotalBytes - scannedBytes));
                    var requested = Min((nuint)ChunkSize, remainingRegion, remainingBudget);
                    if (requested == 0)
                    {
                        reachedLimit = true;
                        return new ProcessMemoryScanResult(regionCount, scannedBytes, reachedLimit);
                    }

                    var readAddress = unchecked((nint)(baseAddress + offset));
                    if (NativeMethods.ReadProcessMemory(handle, readAddress, buffer, requested, out var bytesRead) && bytesRead > 0)
                    {
                        var count = checked((int)bytesRead);
                        scannedBytes += count;
                        if (!handler(buffer.AsSpan(0, count), startsRegion))
                        {
                            return new ProcessMemoryScanResult(regionCount, scannedBytes, reachedLimit);
                        }

                        startsRegion = false;
                    }

                    offset += requested;
                }
            }

            reachedLimit = regionCount >= MaximumRegions || started.Elapsed >= MaximumDuration;
            return new ProcessMemoryScanResult(regionCount, scannedBytes, reachedLimit);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            handle.Dispose();
        }
    }

    private static bool HasSameLiveIdentity(WeixinProcessMemoryIdentity expected)
    {
        try
        {
            using var process = Process.GetProcessById(expected.Process.ProcessId);
            return string.Equals(process.ProcessName, expected.Process.ProcessName, StringComparison.OrdinalIgnoreCase)
                && process.SessionId == expected.SessionId
                && process.StartTime.ToUniversalTime() == expected.StartedAtUtc
                && string.Equals(process.MainModule?.FileName, expected.ImagePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static bool IsReadable(MemoryBasicInformation information)
    {
        if (information.State != MemCommit || (information.Protect & (PageGuard | PageNoAccess)) != 0)
        {
            return false;
        }

        return ReadableProtections.Contains(information.Protect & ProtectionBaseMask);
    }

    private static nuint Min(nuint first, nuint second, nuint third) =>
        Math.Min(first, Math.Min(second, third));
}
