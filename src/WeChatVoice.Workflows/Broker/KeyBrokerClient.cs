using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Core.Protocol;

namespace WeChatVoice.Workflows.Broker;

/// <summary>
/// Normal-privilege client for the one-shot elevated Key Broker. It verifies
/// the Broker binary through an <see cref="IBrokerTrustPolicy"/>, launches it
/// elevated (runas), binds the one-time named pipe to the exact process it
/// started, and translates the terminal response into typed failures. Hosts
/// never see raw broker strings; cancellation kills the elevated process so no
/// privileged worker lingers.
/// </summary>
public sealed class KeyBrokerClient : IBrokerClient
{
    private const string PipePrefix = "WeChatVoice.KeyBroker.";
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(20);
    private readonly IBrokerTrustPolicy _trustPolicy;
    private readonly string? _brokerDirectory;

    public KeyBrokerClient(IBrokerTrustPolicy? trustPolicy = null, string? brokerDirectory = null)
    {
        _trustPolicy = trustPolicy ?? new ReleaseBrokerTrustPolicy();
        _brokerDirectory = brokerDirectory;
    }

    public async Task<BrokerResponse> AcquireAndMaterializeAsync(
        VerifiedRawSnapshot snapshot,
        string snapshotManifestPath,
        string outputRoot,
        string workspaceOutput,
        CancellationToken cancellationToken,
        IProgress<OperationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var brokerPath = Path.Combine(_brokerDirectory ?? AppContext.BaseDirectory, "WeChatVoice.KeyBroker.exe");
        if (!File.Exists(brokerPath))
        {
            throw new FileNotFoundException("The fixed WeChatVoice.KeyBroker.exe was not installed next to the host.", brokerPath);
        }

        VerifyBrokerBinary(brokerPath, _trustPolicy);

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
                using var framedReader = new BoundedLineReader(reader, 16 * 1024);
                var requestJson = JsonSerializer.Serialize(new
                {
                    protocolVersion = 1,
                    requestId,
                    snapshotId = snapshot.SnapshotId,
                    operation = "acquire-and-materialize",
                }) + Environment.NewLine;
                await writer.WriteAsync(requestJson.AsMemory(), operationTimeout.Token).ConfigureAwait(false);

                BrokerResponse? response = null;
                while (response is null)
                {
                    var responseLine = await framedReader.ReadAsync(operationTimeout.Token).ConfigureAwait(false)
                        ?? throw new InvalidDataException("The Key Broker closed without a response.");
                    using var document = JsonDocument.Parse(responseLine);
                    if (document.RootElement.TryGetProperty("stage", out _))
                    {
                        // Stage events contain only progress counters and are not
                        // part of the final operation result. Forward them to
                        // the caller without exposing any broker-owned data.
                        progress?.Report(ToOperationProgress(ParseStage(document.RootElement)));
                        continue;
                    }

                    response = JsonSerializer.Deserialize<BrokerResponse>(responseLine, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                        ?? throw new InvalidDataException("The Key Broker response was empty.");
                }

                if (!string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The Key Broker response RequestId did not match.");
                }

                await process.WaitForExitAsync(operationTimeout.Token).ConfigureAwait(false);
                if (response.Error is not null)
                {
                    throw TranslateError(response.Error);
                }

                return response;
            }
            catch
            {
                TryKill(process);
                throw;
            }
        }
    }

    /// <summary>
    /// Maps the broker's typed wire failure to the typed exception contract:
    /// domain codes become <see cref="AppFailureException"/> with an
    /// <see cref="ErrorCode"/>, transport codes become
    /// <see cref="BrokerTransportException"/>. Unknown codes map to
    /// <see cref="BrokerTransportErrorCode.Unknown"/> instead of leaking a raw
    /// string to a host.
    /// </summary>
    private static Exception TranslateError(BrokerError error)
    {
        switch (error.Kind)
        {
            case BrokerErrorKind.Domain
                when Enum.TryParse<ErrorCode>(error.Code, ignoreCase: true, out var domainCode):
                return new AppFailureException(domainCode, error.Message);
            case BrokerErrorKind.Transport
                when Enum.TryParse<BrokerTransportErrorCode>(error.Code, ignoreCase: true, out var transportCode):
                return new BrokerTransportException(transportCode, error.Message);
            default:
                return new BrokerTransportException(BrokerTransportErrorCode.Unknown, error.Message);
        }
    }

    private static OperationProgress ToOperationProgress(BrokerStageEvent stage)
    {
        (string StageId, string? Message, double? Percent) progress = stage.Stage switch
        {
            "snapshot-staged" => (OperationStageIds.VerifyingSnapshot, "快照已校验", null),
            "profile-selected" => (OperationStageIds.AcquiringKey, "已选择密钥提取 Profile", null),
            "process-locating" => (OperationStageIds.AcquiringKey, "正在定位 Weixin 进程", null),
            "process-matched" => (OperationStageIds.AcquiringKey, "已匹配 Weixin 进程", null),
            "process-verified" => (OperationStageIds.AcquiringKey, "Weixin 进程身份已验证", null),
            "keys-validated" => (OperationStageIds.AcquiringKey, "密钥已通过逐库校验", null),
            "memory-scan" => (OperationStageIds.AcquiringKey, "正在受限内存扫描", null),
            "key-validation" => (OperationStageIds.AcquiringKey, "正在校验密钥候选", null),
            "materializing" => (OperationStageIds.Materializing, "正在解密物料化数据库", ComputePercent(stage)),
            "worker-succeeded" => (OperationStageIds.Materializing, "SQLCipher Worker 已完成", null),
            _ => (stage.Stage, null, null),
        };
        return new OperationProgress(OperationPhase.Materialization, OperationStatus.Running, new OperationStage(progress.StageId, progress.Message, progress.Percent));
    }

    private static double? ComputePercent(BrokerStageEvent stage)
        => stage.TotalDatabases is > 0 && stage.CompletedDatabases is { } completed
            ? completed * 100.0 / stage.TotalDatabases.Value
            : null;

    private static void VerifyBrokerBinary(string path, IBrokerTrustPolicy trustPolicy)
    {
        var result = trustPolicy.Verify(path);
        if (!result.Verified)
        {
            throw new AppFailureException(
                ErrorCode.WorkerBundleUntrusted,
                $"The Key Broker failed trust verification: {result.NonSensitiveReason}");
        }
    }

    private static BrokerStageEvent ParseStage(JsonElement root)
    {
        var stage = root.GetProperty("stage").GetString();
        if (string.IsNullOrWhiteSpace(stage))
        {
            throw new InvalidDataException("The Key Broker stage event did not contain a stage name.");
        }

        return new BrokerStageEvent(
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
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
        }
    }
}
