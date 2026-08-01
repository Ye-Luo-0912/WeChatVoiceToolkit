namespace WeChatVoice.Workflows.Broker;

/// <summary>
/// Trust policy for unsigned development builds. It is never the default: a
/// host must explicitly opt in with --allow-development-broker (or the
/// equivalent UI setting), and the Broker must still be a regular file inside
/// a verified repository build output directory. Release behavior is unchanged
/// and never accepts this policy.
/// </summary>
public sealed class DevelopmentBrokerTrustPolicy : IBrokerTrustPolicy
{
    internal const string SolutionFileName = "WeChatVoice.slnx";

    public BrokerTrustResult Verify(string brokerPath)
    {
        var fullPath = Path.GetFullPath(brokerPath);
        if (!File.Exists(fullPath) || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            return BrokerTrustResult.Deny("broker-not-regular-file");
        }

        var repositoryRoot = FindAncestorWithFile(Path.GetDirectoryName(fullPath), SolutionFileName);
        if (repositoryRoot is null)
        {
            return BrokerTrustResult.Deny("broker-outside-verified-repository");
        }

        var normalizedPath = fullPath.Replace(Path.DirectorySeparatorChar, '/');
        var normalizedRoot = Path.GetFullPath(repositoryRoot).Replace(Path.DirectorySeparatorChar, '/').TrimEnd('/');
        // Any project's bin output under src (the CLI copies the Broker into
        // its own output directory) or a published artifacts directory is a
        // verified repository build output.
        var insideRepositoryBuild = normalizedPath.Contains("/src/", StringComparison.OrdinalIgnoreCase)
            && normalizedPath.Contains("/bin/", StringComparison.OrdinalIgnoreCase);
        var insideArtifacts = normalizedPath.StartsWith(normalizedRoot + "/artifacts/", StringComparison.OrdinalIgnoreCase);
        if (!insideRepositoryBuild && !insideArtifacts)
        {
            return BrokerTrustResult.Deny("broker-not-in-repository-build-directory");
        }

        return BrokerTrustResult.Ok();
    }

    private static string? FindAncestorWithFile(string? startDirectory, string fileName)
    {
        var current = startDirectory is null ? null : new DirectoryInfo(startDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, fileName)))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }
}
