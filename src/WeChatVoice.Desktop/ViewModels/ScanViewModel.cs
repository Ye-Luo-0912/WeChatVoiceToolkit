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
        : this(services, invokeOnUi: null)
    {
    }

    /// <summary>Test seam: an awaitable UI dispatcher runs without Avalonia.</summary>
    internal ScanViewModel(DesktopServices services, Func<Action, Task>? invokeOnUi)
        : base(services, invokeOnUi)
    {
    }

    public override string Title => "语音扫描";

    public override bool CanNavigate
        => Services.Project.Workspace is not null && Services.Project.SelectedContact is not null;

    public override string? NavigationHint => CanNavigate ? null : Services.Project.Workspace is null
        ? "请先完成物料化或加载 Workspace"
        : "请先在联系人页显式选择联系人";

    public bool DurationAnalysisAvailable => Services.Workflows.DurationAnalysisAvailable;

    [ObservableProperty]
    private string? _workspacePath;

    [ObservableProperty]
    private string? _contactUsername;

    [ObservableProperty]
    private string? _directionText = VoiceDirection.Incoming.ToString();

    [ObservableProperty]
    private string? _fromText;

    [ObservableProperty]
    private string? _toText;

    [ObservableProperty] private bool _deepScan;
    [ObservableProperty] private bool _resolveDurations;
    [ObservableProperty] private string? _maximumResultsText;

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
    private int _exportableVoiceCount;

    [ObservableProperty]
    private int _rejectedVoiceCount;

    [ObservableProperty]
    private long _totalPayloadBytes;

    [ObservableProperty]
    private long _totalDurationMs;

    [ObservableProperty]
    private int _durationKnownCount;

    [ObservableProperty]
    private int _durationUnknownCount;

    [ObservableProperty]
    private string? _accountSummary;

    [RelayCommand]
    private Task ScanAsync()
    {
        var selected = Services.Project.SelectedContact;
        var workspacePath = string.IsNullOrWhiteSpace(WorkspacePath) ? Services.Project.WorkspacePath : WorkspacePath;
        var contactUsername = selected?.Username;
        var directionText = DirectionText;
        var fromText = FromText;
        var toText = ToText;
        var maximumResultsText = MaximumResultsText;
        var direction = ParseDirection(directionText);
        var from = VoiceQueryBuilder.ParseUtc(fromText, "from");
        var to = VoiceQueryBuilder.ParseUtc(toText, "to");
        var maximumResults = ParseMaximumResults();
        var deepScan = DeepScan;
        var resolveDurations = ResolveDurations;
        return RunHost.RunAsync(
        async (context, cancellationToken) =>
        {
            if (selected is null || string.IsNullOrWhiteSpace(selected.Username))
            {
                throw new AppFailureException(ErrorCode.InvalidRequest, "Please select a contact before scanning.");
            }

            if (string.IsNullOrWhiteSpace(workspacePath))
            {
                throw new AppFailureException(ErrorCode.InvalidRequest, "Workspace path is required.");
            }

            return await Workflows.VoiceScan.RunAsync(
                new VoiceScanWorkflowRequest(
                    workspacePath,
                    ContactUsername: contactUsername,
                    ConversationId: null,
                    Direction: direction,
                    From: from,
                    To: to,
                    MaximumResults: maximumResults, DeepScan: deepScan, ResolveDurations: resolveDurations),
                context,
                cancellationToken).ConfigureAwait(false);
        },
        result =>
        {
            if (!IsCurrentRequest(selected, workspacePath, directionText, fromText, toText, maximumResultsText, deepScan, resolveDurations))
            {
                throw new AppFailureException(ErrorCode.InvalidRequest, "扫描期间联系人或查询参数发生变化；请重新扫描当前选择。");
            }

            Services.Project.Scan = result;
            Services.Project.Workspace = result.Workspace;
            var report = result.Report;
            var contact = selected!;
            var dataSetId = result.Workspace.DataSet.DataSetId ?? "";
            var accountId = result.Workspace.DataSet.AccountId ?? "";
            var fingerprint = VoiceSelectionPlan.ComputeFingerprint(result.Workspace.Workspace.WorkspaceId, dataSetId, accountId,
                contact.ContactId, contact.Username!, direction, from, to, maximumResults);
            Services.Project.SelectionPlan = new VoiceSelectionPlan(result.Workspace.Workspace.WorkspaceId, dataSetId, accountId,
                contact.ContactId, contact.Username!, direction, from, to, maximumResults, fingerprint, report);
            MatchedVoiceCount = report.MatchedVoiceCount;
            MissingCount = report.UnassociatedMediaCount;
            EmptyCount = report.EmptyBlobCount;
            InvalidHeaderCount = report.InvalidHeaderCount;
            AmbiguousCount = report.AmbiguousPayloadCount;
            DuplicateCount = report.SuspectedDuplicateCount;
            ExportableVoiceCount = report.ExportableVoiceCount;
            RejectedVoiceCount = report.RejectedVoiceCount;
            TotalPayloadBytes = report.TotalPayloadBytes;
            TotalDurationMs = report.TotalDurationMs;
            DurationKnownCount = report.DurationKnownCount;
            DurationUnknownCount = report.DurationUnknownCount;
            AccountSummary = $"账号：{result.Workspace.DataSet.AccountId ?? "（未绑定）"}";
            ScanSummary = $"扫描完成：可导出 {report.ExportableVoiceCount} 条 / 匹配 {report.MatchedVoiceCount} 条；总时长 {report.TotalDurationMs} ms（已解析 {report.DurationKnownCount}，未知 {report.DurationUnknownCount}）；Missing {MissingCount} / Empty {EmptyCount} / InvalidHeader {InvalidHeaderCount} / Ambiguous {AmbiguousCount}；重复统计：{(deepScan ? DuplicateCount.ToString() : "未执行")}";
        });
    }

    private VoiceDirection ParseDirection(string? directionText)
    {
        return Enum.TryParse<VoiceDirection>(directionText, true, out var direction)
            ? direction
            : throw new AppFailureException(ErrorCode.InvalidRequest, "Direction must be incoming or outgoing.");
    }

    partial void OnDirectionTextChanged(string? value) => Services.Project.SelectionPlan = null;
    partial void OnFromTextChanged(string? value) => Services.Project.SelectionPlan = null;
    partial void OnToTextChanged(string? value) => Services.Project.SelectionPlan = null;
    partial void OnDeepScanChanged(bool value) => Services.Project.SelectionPlan = null;
    partial void OnMaximumResultsTextChanged(string? value) => Services.Project.SelectionPlan = null;
    partial void OnResolveDurationsChanged(bool value) => Services.Project.SelectionPlan = null;
    private int? ParseMaximumResults() => ParseMaximumResults(MaximumResultsText);

    private static int? ParseMaximumResults(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : int.TryParse(value, out var parsed) && parsed > 0
                ? parsed
                : throw new AppFailureException(ErrorCode.InvalidRequest, "Maximum results must be a positive integer.");

    private bool IsCurrentRequest(
        ContactRecord? selected,
        string? workspacePath,
        string? directionText,
        string? fromText,
        string? toText,
        string? maximumResultsText,
        bool deepScan,
        bool resolveDurations)
        => ReferenceEquals(Services.Project.SelectedContact, selected)
            && string.Equals(
                string.IsNullOrWhiteSpace(WorkspacePath) ? Services.Project.WorkspacePath : WorkspacePath,
                workspacePath,
                StringComparison.Ordinal)
            && string.Equals(DirectionText, directionText, StringComparison.Ordinal)
            && string.Equals(FromText, fromText, StringComparison.Ordinal)
            && string.Equals(ToText, toText, StringComparison.Ordinal)
            && string.Equals(MaximumResultsText, maximumResultsText, StringComparison.Ordinal)
            && DeepScan == deepScan
            && ResolveDurations == resolveDurations;
}
