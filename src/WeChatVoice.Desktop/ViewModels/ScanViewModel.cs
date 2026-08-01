using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeChatVoice.Core.Errors;
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

    public override bool CanNavigate => Services.Project.Workspace is not null;

    public override string? NavigationHint => CanNavigate ? null : "请先完成物料化或加载 Workspace";

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
    private Task ScanAsync() => RunHost.RunAsync(
        async (context, cancellationToken) =>
        {
            var workspacePath = string.IsNullOrWhiteSpace(WorkspacePath)
                ? Services.Project.WorkspacePath
                : WorkspacePath;
            if (string.IsNullOrWhiteSpace(workspacePath))
            {
                throw new AppFailureException(ErrorCode.InvalidRequest, "Workspace path is required.");
            }

            return await Workflows.VoiceScan.RunAsync(
                new VoiceScanWorkflowRequest(
                    workspacePath,
                    ContactUsername: string.IsNullOrWhiteSpace(ContactUsername) ? Services.Project.SelectedContact?.Username : ContactUsername,
                    ConversationId: null,
                    Direction: ParseDirection(),
                    From: VoiceQueryBuilder.ParseUtc(FromText, "from"),
                    To: VoiceQueryBuilder.ParseUtc(ToText, "to")),
                context,
                cancellationToken).ConfigureAwait(false);
        },
        result =>
        {
            Services.Project.Scan = result;
            Services.Project.Workspace = result.Workspace;
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
            : throw new AppFailureException(ErrorCode.InvalidRequest, "Direction must be incoming or outgoing.");
    }
}
