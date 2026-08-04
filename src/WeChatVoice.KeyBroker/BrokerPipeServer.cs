using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Protocol;
using WeChatVoice.Infrastructure.Trust;

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

        var callerSid = BrokerClientIdentityVerifier.Verify(pipe.SafePipeHandle);

        // The UAC/pipe connection budget is deliberately not reused for the
        // expensive memory scan and materialization operation.
        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationTimeout.CancelAfter(OperationTimeout);
        using var brokerCancellation = CancellationTokenSource.CreateLinkedTokenSource(operationTimeout.Token);
        var disconnectMonitor = MonitorClientConnectionAsync(pipe, brokerCancellation);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false, true), 4096, leaveOpen: true) { AutoFlush = true };
        try
        {
            return await BrokerHost.RunAsync(
                reader,
                writer,
                snapshotManifestPath,
                outputRoot,
                workspaceOutput,
                brokerCancellation.Token,
                allowExperimentalProfile,
                stage => BrokerProtocol.Write(writer, stage),
                callerSid).ConfigureAwait(false);
        }
        finally
        {
            brokerCancellation.Cancel();
            await disconnectMonitor.ConfigureAwait(false);
        }
    }

    internal static async Task<int> RunSelfTestAsync(
        string pipeToken,
        string workerDirectory,
        CancellationToken cancellationToken)
    {
        ValidatePipeToken(pipeToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerDirectory);
        if (!Path.IsPathFullyQualified(workerDirectory))
        {
            throw new ArgumentException("The Worker directory must be absolute.", nameof(workerDirectory));
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The Key Broker self-test is Windows-only.");
        }

        var pipeSecurity = BrokerPipeSecurity.CreateForCurrentUser();
        await using var pipe = NamedPipeServerStreamAcl.Create(
            PipePrefix + pipeToken.ToLowerInvariant(),
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            BrokerProtocol.MaximumRequestLength,
            BrokerProtocol.MaximumResponseLength,
            pipeSecurity);
        using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectionTimeout.CancelAfter(ConnectionTimeout);
        await pipe.WaitForConnectionAsync(connectionTimeout.Token).ConfigureAwait(false);
        var caller = BrokerClientIdentityVerifier.VerifyDetailed(pipe.SafePipeHandle);
        if (string.IsNullOrWhiteSpace(caller.Sid))
        {
            throw new UnauthorizedAccessException("The Broker self-test could not verify the named-pipe client SID.");
        }

        using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false, true), 4096, leaveOpen: true) { AutoFlush = true };
        using var framedReader = new BoundedLineReader(reader, BrokerProtocol.MaximumRequestLength);
        var line = await framedReader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The Broker self-test request was empty.");
        var request = ParseSelfTestRequest(line);
        var worker = await WorkerBundleTrustEvaluator.VerifyAsync(workerDirectory, cancellationToken).ConfigureAwait(false);
        var workerSelfTest = worker.Verified
            ? await RunWorkerSelfTestAsync(workerDirectory, cancellationToken).ConfigureAwait(false)
            : WorkerSelfTestResult.Failed("bundle-untrusted");
        var completed = worker.Verified && workerSelfTest.Completed;
        var response = new BrokerSelfTestResponse(
            completed ? "completed" : "failed",
            request.RequestId,
            Environment.ProcessId,
            worker.Verified ? "verified" : "untrusted",
            workerSelfTest.NonSensitiveReason ?? worker.NonSensitiveReason,
            caller.ProcessId,
            caller.Sid,
            workerSelfTest.Status);
        await writer.WriteLineAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return completed ? 0 : 3;
    }

    private static async Task<WorkerSelfTestResult> RunWorkerSelfTestAsync(
        string workerDirectory,
        CancellationToken cancellationToken)
    {
        var workerPath = Path.Combine(workerDirectory, "WeChatVoice.SqlCipherWorker.exe");
        if (!File.Exists(workerPath)
            || (File.GetAttributes(workerPath) & FileAttributes.ReparsePoint) != 0)
        {
            return WorkerSelfTestResult.Failed("worker-unavailable");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = workerPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workerDirectory,
            },
        };
        process.StartInfo.ArgumentList.Add("--self-test");
        try
        {
            if (!process.Start())
            {
                return WorkerSelfTestResult.Failed("worker-start-failed");
            }

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0
                ? WorkerSelfTestResult.CompletedResult()
                : WorkerSelfTestResult.Failed("worker-self-test-failed");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            TryKill(process);
            return WorkerSelfTestResult.Failed("worker-self-test-timeout");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException
            or UnauthorizedAccessException)
        {
            TryKill(process);
            return WorkerSelfTestResult.Failed("worker-self-test-unavailable");
        }
    }

    private static void TryKill(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException)
        {
        }
    }

    private sealed record WorkerSelfTestResult(bool Completed, string Status, string? NonSensitiveReason)
    {
        public static WorkerSelfTestResult CompletedResult() => new(true, "completed", null);
        public static WorkerSelfTestResult Failed(string reason) => new(false, "failed", reason);
    }

    /// <summary>
    /// The one-shot client has no second control channel. Closing the named
    /// pipe is therefore the cancellation protocol: a disconnected client
    /// cancels the elevated operation instead of leaving memory scanning or
    /// database materialization running until the long operation timeout.
    /// </summary>
    private static async Task MonitorClientConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationTokenSource brokerCancellation)
    {
        try
        {
            while (!brokerCancellation.IsCancellationRequested)
            {
                if (!pipe.IsConnected)
                {
                    brokerCancellation.Cancel();
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), brokerCancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (brokerCancellation.IsCancellationRequested)
        {
        }
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

    private static SelfTestRequest ParseSelfTestRequest(string line)
    {
        using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("protocolVersion", out var version)
            || version.ValueKind != JsonValueKind.Number
            || version.GetInt32() != 1
            || !document.RootElement.TryGetProperty("requestId", out var requestId)
            || requestId.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(requestId.GetString())
            || !document.RootElement.TryGetProperty("operation", out var operation)
            || !string.Equals(operation.GetString(), "self-test", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Broker self-test request is invalid.");
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name is not ("protocolVersion" or "requestId" or "operation"))
            {
                throw new InvalidDataException("The Broker self-test request contains an unsupported field.");
            }
        }

        return new SelfTestRequest(requestId.GetString()!);
    }

    private sealed record SelfTestRequest(string RequestId);
}
