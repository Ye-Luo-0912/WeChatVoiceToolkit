using WeChatVoice.Infrastructure.Serialization;
using WeChatVoice.Windows;

namespace WeChatVoice.Cli.Services.BrokerTrust;

/// <summary>
/// Full trust chain for released Broker binaries: a regular adjacent file,
/// a hash-bound publish manifest, a WinVerifyTrust-validated Authenticode
/// signature pinned to the manifest publisher thumbprint, and a non-user-
/// writable install directory. Every check fails closed.
/// </summary>
internal sealed class ReleaseBrokerTrustPolicy : IBrokerTrustPolicy
{
    private readonly IAuthenticodeVerifier _verifier;
    private readonly string _installDirectory;
    private readonly Func<string, bool> _isUserWritable;

    internal ReleaseBrokerTrustPolicy(IAuthenticodeVerifier? verifier = null, string? installDirectory = null, Func<string, bool>? isUserWritable = null)
    {
        _verifier = verifier ?? AuthenticodeVerifier.Instance;
        _installDirectory = Path.GetFullPath(installDirectory ?? AppContext.BaseDirectory);
        _isUserWritable = isUserWritable ?? IsUserWritable;
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

        if (!BrokerBundleManifestLoader.VerifySidecars(_installDirectory, manifest))
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

        if (_isUserWritable(_installDirectory))
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
        catch (IOException)
        {
            return false;
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
