using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeChatVoice.Core.Models;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Desktop.ViewModels;

/// <summary>
/// Export page: raw SILK only (no decode), exactly one 1:1 contact, one
/// streaming read per source BLOB. Supports cancel and retry; the run journal
/// makes partial failures visible and repeat runs skip hash-verified files.
/// </summary>
public sealed partial class ExportViewModel : PageViewModelBase
{
    public ExportViewModel(DesktopServices services)
        : this(services, marshal: null)
    {
    }

    /// <summary>Test seam: a direct marshaler runs without a UI dispatcher.</summary>
    internal ExportViewModel(DesktopServices services, Action<Action>? marshal)
        : base(services, marshal)
    {
    }

    public override string Title => "语音导出";

    [ObservableProperty]
    private string? _workspacePath;

    [ObservableProperty]
    private string? _contactUsername;

    [ObservableProperty]
    private string? _outputDirectory;

    [ObservableProperty]
    private string? _directionText;

    [ObservableProperty]
    private string? _fromText;

    [ObservableProperty]
    private string? _toText;

    [ObservableProperty]
    private string? _exportSummary;

    [ObservableProperty]
    private int _exportedCount;

    [ObservableProperty]
    private int _skippedCount;

    [ObservableProperty]
    private int _failureCount;

    [ObservableProperty]
    private IReadOnlyList<VoiceExportFailure> _failures = [];

    [ObservableProperty]
    private string? _manifestPath;

    [RelayCommand]
    private Task ExportAsync() => RunHost.RunAsync(async (context, cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(WorkspacePath) || string.IsNullOrWhiteSpace(OutputDirectory))
        {
            throw new ArgumentException("请填写 Workspace 与输出目录。");
        }

        var result = await Workflows.VoiceExport.RunAsync(
            new VoiceExportWorkflowRequest(
                WorkspacePath,
                OutputDirectory,
                ContactUsername: string.IsNullOrWhiteSpace(ContactUsername) ? null : ContactUsername,
                ConversationId: null,
                Direction: ParseDirection(),
                From: VoiceQueryBuilder.ParseUtc(FromText, "from"),
                To: VoiceQueryBuilder.ParseUtc(ToText, "to")),
            context,
            cancellationToken).ConfigureAwait(false);
        var manifest = result.Manifest;
        ExportedCount = manifest.Entries.Count(static entry => !entry.WasSkipped);
        SkippedCount = manifest.Entries.Count(static entry => entry.WasSkipped);
        Failures = manifest.Failures;
        FailureCount = manifest.Failures.Count;
        ExportSummary = manifest.RunStatus switch
        {
            ExportRunStatus.Completed => $"导出完成：新增 {ExportedCount} 条，跳过 {SkippedCount} 条",
            ExportRunStatus.CompletedWithFailures => $"导出完成（含失败）：新增 {ExportedCount} 条，失败 {FailureCount} 条",
            ExportRunStatus.Cancelled => $"导出已取消：已完成 {ExportedCount} 条",
            _ => $"导出失败：{FailureCount} 条",
        };
    });

    /// <summary>Recovers a manifest from a flushed run journal after a crash.</summary>
    [RelayCommand]
    private async Task RecoverAsync(string? journalPath)
    {
        if (string.IsNullOrWhiteSpace(journalPath))
        {
            LastError = "请填写 journal 路径";
            return;
        }

        try
        {
            var manifest = await Workflows.VoiceExport.RecoverRunAsync(journalPath, CancellationToken.None).ConfigureAwait(false);
            ExportedCount = manifest.Entries.Count(static entry => !entry.WasSkipped);
            SkippedCount = manifest.Entries.Count(static entry => entry.WasSkipped);
            Failures = manifest.Failures;
            ExportSummary = $"Journal 恢复完成：{ExportedCount} 条，失败 {manifest.Failures.Count} 条";
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
        }
    }

    [ObservableProperty]
    private string? _journalPath;

    [ObservableProperty]
    private string? _lastError;

    private VoiceDirection? ParseDirection()
    {
        if (string.IsNullOrWhiteSpace(DirectionText))
        {
            return null;
        }

        return Enum.TryParse<VoiceDirection>(DirectionText, true, out var direction)
            ? direction
            : throw new ArgumentException("方向必须是 incoming 或 outgoing。");
    }
}
