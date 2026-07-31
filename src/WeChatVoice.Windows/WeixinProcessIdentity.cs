using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;

namespace WeChatVoice.Windows;

public sealed record WeixinProcessIdentityEvidence(
    int ProcessId,
    string ProcessName,
    DateTimeOffset StartedAtUtc,
    string ImagePath,
    string ImageSha256,
    string ProductVersion,
    bool HasTrustedSignature,
    string SignerSubject,
    string OwnerSid,
    int SessionId,
    string Architecture);

public interface IWeixinProcessIdentityReader
{
    WeixinProcessIdentityEvidence Read(int processId);
}

/// <summary>Locates only the fixed Weixin desktop executable.</summary>
public sealed class WeixinProcessLocator
{
    public IReadOnlyList<WeChatProcessInfo> Locate() =>
        WeChatProcessDiscovery.ListRunning()
            .Where(process => string.Equals(process.ProcessName, "Weixin", StringComparison.OrdinalIgnoreCase))
            .ToArray();
}

/// <summary>
/// Reads identity evidence with a query-only handle. Trust verification is
/// performed by WinVerifyTrust; no process-memory right is requested here.
/// </summary>
public sealed class WindowsWeixinProcessIdentityReader : IWeixinProcessIdentityReader
{
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;

    public WeixinProcessIdentityEvidence Read(int processId)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        using var process = Process.GetProcessById(processId);
        if (!string.Equals(process.ProcessName, "Weixin", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The fixed Weixin process was not found.");
        }

        var native = NativeMethods.OpenProcess(ProcessAccessRights.QueryLimitedInformation, false, checked((uint)processId));
        using var handle = SafeProcessHandle.FromNativeHandle(native);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var imagePath = process.MainModule?.FileName ?? throw new InvalidDataException("The Weixin image path was unavailable.");
        var version = FileVersionInfo.GetVersionInfo(imagePath).ProductVersion
            ?? throw new InvalidDataException("The Weixin product version was unavailable.");
        var imageSha256 = ComputeSha256(imagePath);
        var (trusted, signer) = VerifySignature(imagePath);
        return new WeixinProcessIdentityEvidence(
            process.Id,
            process.ProcessName,
            process.StartTime.ToUniversalTime(),
            Path.GetFullPath(imagePath),
            imageSha256,
            version,
            trusted,
            signer,
            ReadOwnerSid(handle),
            process.SessionId,
            ReadArchitecture(handle));
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 128 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    [SupportedOSPlatform("windows")]
    private static string ReadOwnerSid(SafeProcessHandle process)
    {
        if (!NativeMethods.OpenProcessToken(process, TokenQuery, out var token))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            _ = NativeMethods.GetTokenInformation(token, TokenUser, 0, 0, out var required);
            if (required == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var buffer = Marshal.AllocHGlobal(checked((int)required));
            try
            {
                if (!NativeMethods.GetTokenInformation(token, TokenUser, buffer, required, out _))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                var sidPointer = Marshal.ReadIntPtr(buffer);
                return new SecurityIdentifier(sidPointer).Value;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _ = NativeMethods.CloseHandle(token);
        }
    }

    private static string ReadArchitecture(SafeProcessHandle process)
    {
        if (!NativeMethods.IsWow64Process2(process, out var processMachine, out var nativeMachine))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return (processMachine == 0 ? nativeMachine : processMachine) switch
        {
            0x8664 => "x64",
            0xAA64 => "arm64",
            0x014C => "x86",
            _ => "unknown",
        };
    }

    private static (bool Trusted, string Subject) VerifySignature(string path)
    {
        var pathPointer = Marshal.StringToCoTaskMemUni(path);
        var fileInfoPointer = nint.Zero;
        var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                Size = checked((uint)Marshal.SizeOf<WinTrustFileInfo>()),
                FilePath = pathPointer,
            };
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var data = new WinTrustData
            {
                Size = checked((uint)Marshal.SizeOf<WinTrustData>()),
                UiChoice = 2,
                RevocationChecks = 0,
                UnionChoice = 1,
                FileInfo = fileInfoPointer,
                StateAction = 0,
                ProviderFlags = 0x00000010,
            };
            var trusted = NativeMethods.WinVerifyTrust(0, ref action, ref data) == 0;
            if (!trusted)
            {
                return (false, string.Empty);
            }

            // WinVerifyTrust authenticates the complete image. CompanyName is
            // then read from the authenticated image as the publisher binding
            // used by the exact Profile policy.
            return (true, FileVersionInfo.GetVersionInfo(path).CompanyName ?? string.Empty);
        }
        finally
        {
            if (fileInfoPointer != 0)
            {
                Marshal.FreeHGlobal(fileInfoPointer);
            }

            Marshal.FreeCoTaskMem(pathPointer);
        }
    }
}
