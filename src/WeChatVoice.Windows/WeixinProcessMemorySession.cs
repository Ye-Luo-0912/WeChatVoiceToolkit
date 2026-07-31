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

internal readonly record struct ProcessMemoryScanBudget
{
    internal ProcessMemoryScanBudget(TimeSpan maximumDuration, long maximumTotalBytes)
    {
        if (maximumDuration <= TimeSpan.Zero || maximumTotalBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumDuration));
        }

        MaximumDuration = maximumDuration;
        MaximumTotalBytes = maximumTotalBytes;
    }

    internal TimeSpan MaximumDuration { get; }

    internal long MaximumTotalBytes { get; }
}

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
    internal static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(60);

    private const uint MemCommit = 0x1000;
    private const uint MemPrivate = 0x20000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;
    private const uint PageReadWrite = 0x04;
    private const uint PageWriteCopy = 0x08;
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
        ProcessMemoryScanBudget budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(disposed, this);

        var started = Stopwatch.StartNew();
        var maximumDuration = budget.MaximumDuration <= MaximumDuration ? budget.MaximumDuration : MaximumDuration;
        var maximumTotalBytes = Math.Min(budget.MaximumTotalBytes, MaximumTotalBytes);
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var regions = new List<MemoryBasicInformation>();
        long scannedBytes = 0;
        var reachedLimit = false;
        try
        {
            nuint address = 0;
            while (regions.Count < MaximumRegions && started.Elapsed < maximumDuration)
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
                if (!IsReadable(information) || regionSize == 0)
                {
                    continue;
                }

                regions.Add(information);
            }

            // Heap/private writable pages are the most likely location for a
            // short-lived ASCII key. Query the complete bounded region list
            // first, then spend the caller's read budget on those regions
            // before image/executable pages. This changes only ordering; all
            // hard region, byte, duration, and cancellation limits remain.
            regions.Sort(static (left, right) =>
            {
                var priority = IsPriority(right).CompareTo(IsPriority(left));
                return priority != 0
                    ? priority
                    : left.BaseAddress.ToInt64().CompareTo(right.BaseAddress.ToInt64());
            });

            foreach (var information in regions)
            {
                if (started.Elapsed >= maximumDuration || scannedBytes >= maximumTotalBytes)
                {
                    reachedLimit = true;
                    return new ProcessMemoryScanResult(regions.Count, scannedBytes, reachedLimit);
                }

                var baseAddress = unchecked((nuint)information.BaseAddress);
                var regionSize = information.RegionSize;
                nuint offset = 0;
                var startsRegion = true;
                while (offset < regionSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (started.Elapsed >= maximumDuration || scannedBytes >= maximumTotalBytes)
                    {
                        reachedLimit = true;
                        return new ProcessMemoryScanResult(regions.Count, scannedBytes, reachedLimit);
                    }

                    var remainingRegion = regionSize - offset;
                    var remainingBudget = checked((nuint)(maximumTotalBytes - scannedBytes));
                    var requested = Min((nuint)ChunkSize, remainingRegion, remainingBudget);
                    if (requested == 0)
                    {
                        reachedLimit = true;
                        return new ProcessMemoryScanResult(regions.Count, scannedBytes, reachedLimit);
                    }

                    var readAddress = unchecked((nint)(baseAddress + offset));
                    if (NativeMethods.ReadProcessMemory(handle, readAddress, buffer, requested, out var bytesRead) && bytesRead > 0)
                    {
                        var count = checked((int)bytesRead);
                        scannedBytes += count;
                        if (!handler(buffer.AsSpan(0, count), startsRegion))
                        {
                            return new ProcessMemoryScanResult(regions.Count, scannedBytes, reachedLimit);
                        }

                        startsRegion = false;
                    }

                    offset += requested;
                }
            }

            reachedLimit = regions.Count >= MaximumRegions || started.Elapsed >= maximumDuration || scannedBytes >= maximumTotalBytes;
            return new ProcessMemoryScanResult(regions.Count, scannedBytes, reachedLimit);
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

    private static bool IsPriority(MemoryBasicInformation information)
    {
        var protection = information.Protect & ProtectionBaseMask;
        return information.Type == MemPrivate && protection is PageReadWrite or PageWriteCopy;
    }

    private static nuint Min(nuint first, nuint second, nuint third) =>
        Math.Min(first, Math.Min(second, third));
}
