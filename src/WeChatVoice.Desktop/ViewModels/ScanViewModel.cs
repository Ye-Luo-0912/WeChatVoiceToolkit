using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeChatVoice.Core.Models;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Desktop.ViewModels;

/// <summary>
/// Voice scan page: metadata-only audit for exactly one 1:1 contact. The four
/// payload states (Missing / Empty / InvalidHeader / Ambiguous) are shown
/// explicitly; only Linked voices are exportable.
/// </summary>
public sealed partial class ScanViewModel : PageViewModelBase
{
    public ScanViewModel(DesktopServices services)
        : this(services, marshal: null)
    {
    }

    /// <summary>Test seam: a direct marshaler runs without a UI dispatcher.</summary>
    internal ScanViewModel(DesktopServices services, Action<Action>? marshal)
        : base(services, marshal)
    {
    }

    public override string Title => "语音扫描";

    [ObservableProperty]
    private string? _workspacePath;

    [ObservableProperty]
    private string? _contactUsername;

    [ObservableProperty]
    private string? _directionText;

    [ObservableProperty]
    private string? _fromText;

    [ObservableProperty]
    private string? _toText;

    [ObservableProperty]
    private string? _scanSummary;

    [ObservableProperty]
    private int _matchedVoiceCount;

    [ObservableProperty]
    private int _missingCount;

    [ObservableProperty]
    private int _emptyCount;

    [ObservableProperty]
    private int _invalidHeaderCount;

    [ObservableProperty]
    private int _ambiguousCount;

    [ObservableProperty]
    private int _duplicateCount;

    [ObservableProperty]
    private string? _accountSummary;

    [RelayCommand]
    private Task ScanAsync() => RunHost.RunAsync(async (context, cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(WorkspacePath))
        {
            throw new ArgumentException("请填写 Workspace 路径。");
        }

        var result = await Workflows.VoiceScan.RunAsync(
            new VoiceScanWorkflowRequest(
                WorkspacePath,
                ContactUsername: string.IsNullOrWhiteSpace(ContactUsername) ? null : ContactUsername,
                ConversationId: null,
                Direction: ParseDirection(),
                From: VoiceQueryBuilder.ParseUtc(FromText, "from"),
                To: VoiceQueryBuilder.ParseUtc(ToText, "to")),
            context,
            cancellationToken).ConfigureAwait(false);
        var report = result.Report;
        MatchedVoiceCount = report.MatchedVoiceCount;
        MissingCount = report.UnassociatedMediaCount;
        EmptyCount = report.EmptyBlobCount;
        InvalidHeaderCount = report.InvalidHeaderCount;
        AmbiguousCount = report.AmbiguousPayloadCount;
        DuplicateCount = report.SuspectedDuplicateCount;
        AccountSummary = $"账号：{result.Workspace.DataSet.AccountId ?? "（未绑定）"}";
        ScanSummary = $"扫描完成：匹配 {report.MatchedVoiceCount} 条；Missing {MissingCount} / Empty {EmptyCount} / InvalidHeader {InvalidHeaderCount} / Ambiguous {AmbiguousCount}；疑似重复 {DuplicateCount}";
    });

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
