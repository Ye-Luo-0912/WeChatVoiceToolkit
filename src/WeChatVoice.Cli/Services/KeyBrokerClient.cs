using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Protocol;

namespace WeChatVoice.Cli.Services;

internal sealed class KeyBrokerClient
{
    private const string PipePrefix = "WeChatVoice.KeyBroker.";
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(20);

    internal async Task<KeyBrokerResult> AcquireAndMaterializeAsync(
        VerifiedRawSnapshot snapshot,
        string snapshotManifestPath,
        string outputRoot,
        string workspaceOutput,
        CancellationToken cancellationToken,
        Action<KeyBrokerStage>? reportStage = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var brokerPath = Path.Combine(AppContext.BaseDirectory, "WeChatVoice.KeyBroker.exe");
        if (!File.Exists(brokerPath))
        {
            throw new FileNotFoundException("The fixed WeChatVoice.KeyBroker.exe was not installed next to the CLI.", brokerPath);
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var requestId = Guid.NewGuid().ToString("N");
        var startInfo = new ProcessStartInfo
        {
            FileName = brokerPath,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add("--pipe-token");
        startInfo.ArgumentList.Add(token);
        startInfo.ArgumentList.Add("--snapshot-manifest");
        startInfo.ArgumentList.Add(Path.GetFullPath(snapshotManifestPath));
        startInfo.ArgumentList.Add("--output-root");
        startInfo.ArgumentList.Add(Path.GetFullPath(outputRoot));
        startInfo.ArgumentList.Add("--workspace-output");
        startInfo.ArgumentList.Add(Path.GetFullPath(workspaceOutput));
        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("The elevated Key Broker did not start.");
        }
        catch (Win32Exception exception)
        {
            throw new UnauthorizedAccessException("The elevated Key Broker could not be started or elevation was declined.", exception);
        }

        using (process)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(
                    ".", PipePrefix + token, PipeDirection.InOut, PipeOptions.Asynchronous);
                using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectionTimeout.CancelAfter(ConnectionTimeout);
                await pipe.ConnectAsync(connectionTimeout.Token).ConfigureAwait(false);
                NamedPipeIdentityVerifier.VerifyServerProcess(pipe.SafePipeHandle, process.Id);
                using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                operationTimeout.CancelAfter(OperationTimeout);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false, true), 4096, leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
                var requestJson = JsonSerializer.Serialize(new
                {
                    protocolVersion = 1,
                    requestId,
                    snapshotId = snapshot.SnapshotId,
                    operation = "acquire-and-materialize",
                }) + Environment.NewLine;
                await writer.WriteAsync(requestJson.AsMemory(), operationTimeout.Token).ConfigureAwait(false);

                KeyBrokerResult? response = null;
                while (response is null)
                {
                    var responseLine = await BoundedLineReader.ReadAsync(reader, 16 * 1024, operationTimeout.Token).ConfigureAwait(false)
                        ?? throw new InvalidDataException("The Key Broker closed without a response.");
                    using var document = JsonDocument.Parse(responseLine);
                    if (document.RootElement.TryGetProperty("stage", out _))
                    {
                        // Stage events contain only progress counters and are not
                        // part of the final operation result. Forward them to
                        // the caller without exposing any broker-owned data.
                        reportStage?.Invoke(ParseStage(document.RootElement));
                        continue;
                    }

                    response = JsonSerializer.Deserialize<KeyBrokerResult>(responseLine, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                        ?? throw new InvalidDataException("The Key Broker response was empty.");
                }

                if (!string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The Key Broker response RequestId did not match.");
                }

                await process.WaitForExitAsync(operationTimeout.Token).ConfigureAwait(false);
                return response;
            }
            catch
            {
                TryKill(process);
                throw;
            }
        }
    }

    private static KeyBrokerStage ParseStage(JsonElement root)
    {
        var stage = root.GetProperty("stage").GetString();
        if (string.IsNullOrWhiteSpace(stage))
        {
            throw new InvalidDataException("The Key Broker stage event did not contain a stage name.");
        }

        return new KeyBrokerStage(
            stage,
            TryGetInt64(root, "scannedBytes"),
            TryGetInt32(root, "candidates"),
            TryGetInt32(root, "completedGroups"),
            TryGetInt32(root, "totalGroups"),
            TryGetInt32(root, "completedDatabases"),
            TryGetInt32(root, "totalDatabases"),
            TryGetInt32(root, "firstUnvalidatedGroupOrdinal"));
    }

    private static long? TryGetInt64(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result)
            ? result
            : null;

    private static int? TryGetInt32(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : null;

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}

internal sealed record KeyBrokerResult(
    string Status,
    string? RequestId,
    string? ProfileId,
    string? MaterializationId,
    KeyBrokerError? Error);

internal sealed record KeyBrokerError(string Code, string Message);

internal sealed record KeyBrokerStage(
    string Stage,
    long? ScannedBytes,
    int? Candidates,
    int? CompletedGroups,
    int? TotalGroups,
    int? CompletedDatabases,
    int? TotalDatabases,
    int? FirstUnvalidatedGroupOrdinal);

internal sealed class KeyBrokerOperationException(string code, string message) : InvalidOperationException(message)
{
    internal string Code { get; } = code;
}
