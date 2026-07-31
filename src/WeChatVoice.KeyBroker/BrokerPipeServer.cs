using System.IO.Pipes;
using System.Text;

namespace WeChatVoice.KeyBroker;

internal static class BrokerPipeServer
{
    internal const string PipePrefix = "WeChatVoice.KeyBroker.";
    internal static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(20);

    internal static async Task<int> RunAsync(
        string pipeToken,
        string snapshotManifestPath,
        string outputRoot,
        string workspaceOutput,
        CancellationToken cancellationToken,
        bool allowExperimentalProfile = false)
    {
        ValidatePipeToken(pipeToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotManifestPath);
        if (!Path.IsPathFullyQualified(snapshotManifestPath))
        {
            throw new ArgumentException("The Snapshot Manifest path must be absolute.", nameof(snapshotManifestPath));
        }

        ValidateOutputPath(outputRoot, nameof(outputRoot));
        ValidateOutputPath(workspaceOutput, nameof(workspaceOutput));

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The Key Broker pipe server is Windows-only.");
        }

        var pipeSecurity = BrokerPipeSecurity.CreateForCurrentUser();
        await using var pipe = NamedPipeServerStreamAcl.Create(
                PipePrefix + pipeToken.ToLowerInvariant(),
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                BrokerProtocol.MaximumRequestLength,
                BrokerProtocol.MaximumRequestLength,
                pipeSecurity);
        using (var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectionTimeout.CancelAfter(ConnectionTimeout);
            await pipe.WaitForConnectionAsync(connectionTimeout.Token).ConfigureAwait(false);
        }

        // The UAC/pipe connection budget is deliberately not reused for the
        // expensive memory scan and materialization operation.
        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationTimeout.CancelAfter(OperationTimeout);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false, true), 4096, leaveOpen: true) { AutoFlush = true };
        return await BrokerHost.RunAsync(
            reader,
            writer,
            snapshotManifestPath,
            outputRoot,
            workspaceOutput,
            operationTimeout.Token,
            allowExperimentalProfile,
            stage => BrokerProtocol.Write(writer, stage)).ConfigureAwait(false);
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
