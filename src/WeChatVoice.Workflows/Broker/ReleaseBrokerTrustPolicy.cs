using System.Security;
using WeChatVoice.Infrastructure.Serialization;
using WeChatVoice.Windows;

namespace WeChatVoice.Workflows.Broker;

public enum UserWriteability
{
    Writable,
    NotWritable,
    Indeterminate,
}

/// <summary>
/// Full trust chain for released Broker binaries: a regular adjacent file,
/// a hash-bound publish manifest, a WinVerifyTrust-validated Authenticode
/// signature pinned to the manifest publisher thumbprint, and a non-user-
/// writable install directory. Every check fails closed. This is the default
/// policy for released installs; unsigned or mismatched brokers are denied.
/// </summary>
public sealed class ReleaseBrokerTrustPolicy : IBrokerTrustPolicy
{
    private readonly IAuthenticodeVerifier _verifier;
    private readonly string _installDirectory;
    private readonly Func<string, bool> _isUserWritable;
    private readonly Func<string, UserWriteability>? _writeabilityProbe;

    public ReleaseBrokerTrustPolicy(IAuthenticodeVerifier? verifier = null, string? installDirectory = null, Func<string, bool>? isUserWritable = null, Func<string, UserWriteability>? writeabilityProbe = null)
    {
        _verifier = verifier ?? AuthenticodeVerifier.Instance;
        _installDirectory = Path.GetFullPath(installDirectory ?? AppContext.BaseDirectory);
        _isUserWritable = isUserWritable ?? IsUserWritable;
        _writeabilityProbe = writeabilityProbe;
    }

    public BrokerTrustResult Verify(string brokerPath)
    {
        var fullPath = Path.GetFullPath(brokerPath);
        if (!File.Exists(fullPath) || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            return BrokerTrustResult.Deny("broker-not-regular-file");
        }

        if (!string.Equals(Path.GetDirectoryName(fullPath), _installDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            return BrokerTrustResult.Deny("broker-outside-install-directory");
        }

        var manifest = BrokerBundleManifestLoader.TryLoad(_installDirectory);
        if (manifest is null)
        {
            return BrokerTrustResult.Deny("broker-bundle-manifest-unavailable");
        }

        if (string.IsNullOrWhiteSpace(manifest.PublisherThumbprint))
        {
            return BrokerTrustResult.Deny("broker-publisher-unpinned");
        }

        var brokerHash = FileHashing.ComputeMetadataAsync(fullPath, CancellationToken.None)
            .GetAwaiter()
            .GetResult()
            .Sha256;
        if (!string.Equals(manifest.BrokerExeSha256, brokerHash, StringComparison.OrdinalIgnoreCase))
        {
            return BrokerTrustResult.Deny("broker-hash-mismatch");
        }

        bool sidecarsVerified;
        try
        {
            sidecarsVerified = BrokerBundleManifestLoader.VerifySidecars(_installDirectory, manifest);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or SecurityException)
        {
            sidecarsVerified = false;
        }

        if (!sidecarsVerified)
        {
            return BrokerTrustResult.Deny("broker-bundle-sidecar-mismatch");
        }

        var signature = _verifier.Verify(fullPath);
        if (!signature.Trusted)
        {
            return BrokerTrustResult.Deny("broker-not-authenticode-signed");
        }

        if (string.IsNullOrWhiteSpace(signature.SignerThumbprint)
            || !string.Equals(signature.SignerThumbprint, manifest.PublisherThumbprint, StringComparison.OrdinalIgnoreCase))
        {
            return BrokerTrustResult.Deny("broker-publisher-mismatch");
        }

        UserWriteability writeability;
        try
        {
            writeability = _writeabilityProbe?.Invoke(_installDirectory)
                ?? (_isUserWritable(_installDirectory) ? UserWriteability.Writable : UserWriteability.NotWritable);
        }
        catch (UnauthorizedAccessException)
        {
            // Access denial is an explicit negative answer for the write
            // probe, not an infrastructure failure. The directory is not
            // writable by this user.
            writeability = UserWriteability.NotWritable;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException or SecurityException)
        {
            // Disk, path, sharing, and ACL-provider failures do not prove
            // that the directory is protected. Fail closed as indeterminate.
            writeability = UserWriteability.Indeterminate;
        }
        if (writeability == UserWriteability.Indeterminate)
        {
            return BrokerTrustResult.Deny("install-directory-writeability-indeterminate");
        }

        if (writeability == UserWriteability.Writable)
        {
            return BrokerTrustResult.Deny("install-directory-user-writable");
        }

        return BrokerTrustResult.Ok();
    }

    /// <summary>
    /// A released install directory must not be writable by the invoking
    /// user. A successful create attempt is authoritative: the directory is
    /// user-writable and therefore unsafe for a trusted elevated binary.
    /// </summary>
    private static bool IsUserWritable(string directory)
    {
        var probe = Path.Combine(directory, $".wechatvoice-write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            TryDelete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException exception) when (exception is not null)
        {
            throw new InvalidOperationException("The install directory writeability probe was indeterminate.", exception);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
