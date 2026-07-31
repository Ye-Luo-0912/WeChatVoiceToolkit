using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WeChatVoice.Core.Models;

namespace WeChatVoice.Cli.Services;

internal sealed class KeyBrokerClient
{
    private const string PipePrefix = "WeChatVoice.KeyBroker.";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    internal async Task<KeyBrokerResult> AcquireAndMaterializeAsync(
        VerifiedRawSnapshot snapshot,
        string snapshotManifestPath,
        string outputRoot,
        string workspaceOutput,
        CancellationToken cancellationToken)
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

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
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
        await using (var pipe = new NamedPipeClientStream(
            ".", PipePrefix + token, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false, true), 4096, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                protocolVersion = 1,
                requestId,
                snapshotId = snapshot.SnapshotId,
                operation = "acquire-and-materialize",
            })).ConfigureAwait(false);
            var responseLine = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The Key Broker closed without a response.");
            if (responseLine.Length > 16 * 1024)
            {
                throw new InvalidDataException("The Key Broker response exceeded its fixed limit.");
            }

            var response = JsonSerializer.Deserialize<KeyBrokerResult>(responseLine, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException("The Key Broker response was empty.");
            if (!string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The Key Broker response RequestId did not match.");
            }

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return response;
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

internal sealed class KeyBrokerOperationException(string code, string message) : InvalidOperationException(message)
{
    internal string Code { get; } = code;
}
