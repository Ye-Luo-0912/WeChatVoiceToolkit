using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WeChatVoice.Windows;

/// <summary>
/// Result of a full Authenticode verification. <see cref="Trusted"/> means
/// WinVerifyTrust accepted the complete image. <see cref="SignerThumbprint"/>
/// is the SHA-256 digest of the signer certificate and <see cref="Publisher"/>
/// is the publisher binding read from the authenticated image.
/// </summary>
public sealed record AuthenticodeSignature(bool Trusted, string? SignerThumbprint, string? Publisher);

/// <summary>Injection seam used by trust policies and tests.</summary>
public interface IAuthenticodeVerifier
{
    AuthenticodeSignature Verify(string path);
}

/// <summary>
/// The single authoritative Authenticode verification path for this codebase.
/// Both the Weixin process identity check and the Broker binary trust policy
/// delegate here; no other component re-implements WinVerifyTrust.
/// </summary>
public sealed class AuthenticodeVerifier : IAuthenticodeVerifier
{
    private static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public static AuthenticodeVerifier Instance { get; } = new();

    public AuthenticodeSignature Verify(string path) => VerifyCore(path);

    private static AuthenticodeSignature VerifyCore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Authenticode verification is Windows-only.");
        }

        var pathPointer = Marshal.StringToCoTaskMemUni(path);
        var fileInfoPointer = nint.Zero;
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
            var action = WinTrustActionGenericVerifyV2;
            var trusted = NativeMethods.WinVerifyTrust(0, ref action, ref data) == 0;
            if (!trusted)
            {
                return new AuthenticodeSignature(false, null, null);
            }

            // WinVerifyTrust authenticates the complete image. The signer
            // certificate is then read from the authenticated image and pinned
            // by the SHA-256 digest of its raw bytes; Publisher keeps the
            // same CompanyName binding the process identity check has always
            // used, so no existing policy semantics change.
            var thumbprint = ReadSignerThumbprint(path);
            var publisher = FileVersionInfo.GetVersionInfo(path).CompanyName ?? string.Empty;
            return new AuthenticodeSignature(true, thumbprint, publisher);
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

    private static string? ReadSignerThumbprint(string path)
    {
        try
        {
            // LoadCertificateFromFile expects a certificate container. A PE
            // Authenticode signature is embedded in the image instead, so use
            // the signed-file API to extract the authenticated signer, then
            // hand its DER bytes to the current certificate loader.
#pragma warning disable SYSLIB0057 // The signed-PE API has no non-obsolete replacement yet.
            using var embeddedCertificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using var certificate = X509CertificateLoader.LoadCertificate(embeddedCertificate.GetRawCertData());
            return Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
        }
        catch (CryptographicException)
        {
            // The image verified, but the embedded certificate could not be
            // read back as an X.509 leaf; the caller must treat this as an
            // unpinnable publisher and fail closed.
            return null;
        }
    }
}
