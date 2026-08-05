using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using KeyProfileMetadataModel = WeChatVoice.KeyProfileMetadata.KeyProfileMetadata;

namespace WeChatVoice.Cli;

internal static partial class CliApplication
{
    static async Task<T> ReadJsonFileAsync<T>(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, CliJson.Options, cancellationToken).ConfigureAwait(false);
        return value ?? throw new InvalidDataException($"The JSON document was empty: '{fullPath}'.");
    }

    static async Task WriteJsonFileAsync<T>(string outputPath, T value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var fullOutputPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullOutputPath)
            ?? throw new ArgumentException("The output path must include a directory.", nameof(outputPath));
        Directory.CreateDirectory(directory);
        var temporaryPath = fullOutputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, value, CliJson.Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, fullOutputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    static void WriteJson<T>(T value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, CliJson.Options));

    static void WriteError(Exception exception)
    {
        // Stable error codes stay machine-readable; the presentation layer owns
        // the localized text. This is the same boundary a UI host uses.
        switch (exception)
        {
            case AppFailureException appException:
                WriteLocalized(appException.Code);
                return;
            case BrokerTransportException brokerException:
                Console.Error.WriteLine($"[{brokerException.Code}] {brokerException.Message}");
                return;
            case NoMatchingDataSetAdapterException:
                WriteLocalized(ErrorCode.UnsupportedSchema);
                return;
            default:
                Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
                return;
        }

        static void WriteLocalized(ErrorCode code)
        {
            var zh = ErrorMessagesZhHans.Get(code);
            Console.Error.WriteLine($"[{code}] {zh.Message}{(zh.SuggestedAction is null ? string.Empty : "（" + zh.SuggestedAction + "）")}");
        }
    }

    static int GetExportExitCode(VoiceExportManifest manifest)
    {
        if (manifest.Failures.Any(static failure => string.Equals(failure.Stage, "query", StringComparison.Ordinal)))
        {
            return 1;
        }

        if (manifest.Entries.Count == 0 && manifest.Failures.Count == 0)
        {
            return 4;
        }

        return manifest.Failures.Count == 0 ? 0 : 3;
    }

    internal sealed record DoctorReport(
        string Runtime,
        string OperatingSystem,
        bool IsWindows,
        IReadOnlyList<string> RecognizedProcessNames,
        IReadOnlyList<WeChatVoice.Windows.WeChatProcessInfo> RunningWeChatProcesses,
        CapabilityReport Capabilities);

    internal sealed record CapabilityReport(
        IReadOnlyList<string> RegisteredAdapters,
        bool AdapterMatchEvaluated,
        IReadOnlyList<string> MatchingAdapters,
        IReadOnlyList<KeyProfileMetadataModel> KeyAcquisitionProfiles,
        IReadOnlyList<string> MatchingKeyAcquisitionProfiles,
        IReadOnlyList<string> MaterializationBackends,
        bool BrokerAcquireAndMaterializeAvailable,
        bool OrdinaryCliCanReadProcessMemory,
        bool AllowsArbitraryProcessMemoryRead,
        bool HasUserInterface);

    internal sealed record SchemaProbeResult(string OutputPath, int ObjectCount);

    internal sealed record DatasetProbeResult(string OutputPath, string DataSetId, int DatabaseCount, int IssueCount, int AdapterCandidateCount);

    internal sealed record WorkspaceCreateResult(string OutputPath, string WorkspaceId, string DataSetId, int DatabaseCount, int IssueCount);

    internal sealed record WorkspaceVerifyResult(string WorkspacePath, string WorkspaceId, string DataSetId, int DatabaseCount, DateTimeOffset VerifiedAtUtc);

    internal sealed record WorkspaceRecoveryResult(
        string OutputRoot,
        string WorkspacePath,
        string WorkspaceId,
        string DataSetId,
        int DatabaseCount,
        DateTimeOffset VerifiedAtUtc);

    internal sealed record WorkspaceMaterializationResult(
        string MaterializationWorkspaceId,
        string OutputRoot,
        string MaterializationManifestPath,
        string LocalWorkspacePath,
        string LocalWorkspaceId,
        string DataSetId,
        int DatabaseCount);

    internal sealed record BrokerWorkspaceMaterializationResult(
        string ProfileId,
        string MaterializationId,
        string LocalWorkspacePath,
        string WorkspaceId,
        string DataSetId,
        int DatabaseCount);

    internal static class CliJson
    {
        internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
    }

    /// <summary>
    /// Presentation-layer mapping from stable error codes to zh-Hans guidance.
    /// The codes themselves are the machine contract; this table is display only.
    /// </summary>
    internal static class ErrorMessagesZhHans
    {
        internal static (string Message, string? SuggestedAction) Get(ErrorCode code) => code switch
        {
            ErrorCode.WeixinNotRunning => ("未检测到正在运行的受支持 Weixin 进程", "start-weixin"),
            ErrorCode.UnsupportedWeixinVersion => ("当前 Weixin 版本不受支持（仅支持 4.1.11.55）", "use-supported-version"),
            ErrorCode.ProcessIdentityMismatch => ("Weixin 进程身份证据与 Profile 不匹配", "restart-weixin-and-retry"),
            ErrorCode.SnapshotInvalid => ("快照校验失败", "re-snapshot"),
            ErrorCode.SnapshotInconsistent => ("快照内容在验证期间发生变化", "re-snapshot"),
            ErrorCode.KeyCandidateNotFound => ("未能为每个数据库组校验出密钥", "restart-weixin-and-retry"),
            ErrorCode.DatabaseGroupUncovered => ("密钥采集未覆盖全部必需的源数据库", "re-snapshot"),
            ErrorCode.WorkerBundleUntrusted => ("SQLCipher Worker 或 Key Broker 未通过信任校验", "reinstall-package"),
            ErrorCode.WorkerFailed => ("SQLCipher Worker 处理失败", "retry-materialization"),
            ErrorCode.MaterializationInvalid => ("物料化输出未通过独立校验", "retry-materialization"),
            ErrorCode.WorkspaceInvalid => ("本地 Workspace 无效或与确认账号不一致", "re-materialize"),
            ErrorCode.UnsupportedSchema => ("未找到支持该数据库结构的已验证适配器", "use-supported-schema"),
            ErrorCode.ContactNotFound => ("未找到请求的稳定联系人", "choose-contact"),
            ErrorCode.ExportPartialFailure => ("导出完成但存在逐条失败", "review-failures"),
            ErrorCode.AccountConfirmationRequired => ("账号身份仅为候选，需要明确确认", "confirm-account"),
            ErrorCode.UacElevationRejected => ("UAC 管理员授权被拒绝", "retry-materialization"),
            ErrorCode.DataSourceDiscoveryFailed => ("自动发现微信数据失败", "retry-discovery"),
            ErrorCode.DataSourceDiscoveryTruncated => ("微信数据发现未完成，结果可能不完整", "retry-discovery"),
            ErrorCode.NoDataSourceFound => ("未自动找到微信数据", "choose-data-directory"),
            ErrorCode.MultipleAccountsRequireSelection => ("发现多个微信账号，需要明确选择", "choose-account"),
            ErrorCode.SelectedDataSourceInvalid => ("选择的微信数据目录无效", "choose-data-directory"),
            ErrorCode.WeixinStillRunning => ("请完全退出微信后再创建稳定快照", "exit-weixin-and-retry"),
            ErrorCode.SnapshotOutputInvalid => ("快照保存位置无效", "choose-snapshot-output"),
            ErrorCode.InsufficientDiskSpace => ("快照保存位置磁盘空间不足", "free-disk-space"),
            _ => ("操作失败", null),
        };
    }
}
