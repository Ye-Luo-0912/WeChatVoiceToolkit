using System.IO.Pipes;
using System.Text;

namespace WeChatVoice.KeyBroker;

internal static class BrokerPipeServer
{
    internal const string PipePrefix = "WeChatVoice.KeyBroker.";
    internal static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(30);

    internal static async Task<int> RunAsync(
        string pipeToken,
        string snapshotManifestPath,
        string outputRoot,
        string workspaceOutput,
        CancellationToken cancellationToken)
    {
        ValidatePipeToken(pipeToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotManifestPath);
        if (!Path.IsPathFullyQualified(snapshotManifestPath))
        {
            throw new ArgumentException("The Snapshot Manifest path must be absolute.", nameof(snapshotManifestPath));
        }

        ValidateOutputPath(outputRoot, nameof(outputRoot));
        ValidateOutputPath(workspaceOutput, nameof(workspaceOutput));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectionTimeout);
        await using var pipe = new NamedPipeServerStream(
            PipePrefix + pipeToken.ToLowerInvariant(),
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            BrokerProtocol.MaximumRequestLength,
            BrokerProtocol.MaximumRequestLength);
        await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false, true), 4096, leaveOpen: true) { AutoFlush = true };
        return await BrokerHost.RunAsync(reader, writer, snapshotManifestPath, outputRoot, workspaceOutput, timeout.Token).ConfigureAwait(false);
    }

    private static void ValidateOutputPath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Broker output paths must be absolute.", parameterName);
        }
    }

    internal static void ValidatePipeToken(string pipeToken)
    {
        if (pipeToken.Length != 64 || !pipeToken.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("The pipe token must be exactly 256 random bits encoded as hex.", nameof(pipeToken));
        }
    }
}
