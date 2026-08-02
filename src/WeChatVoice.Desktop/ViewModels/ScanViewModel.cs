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
    private string? _directionText = VoiceDirection.Incoming.ToString();

    [ObservableProperty]
    private string? _fromText;

    [ObservableProperty]
    private string? _toText;

    [ObservableProperty] private bool _deepScan;
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
    private string? _accountSummary;

    [RelayCommand]
    private Task ScanAsync()
    {
        var selected = Services.Project.SelectedContact;
        var workspacePath = string.IsNullOrWhiteSpace(WorkspacePath) ? Services.Project.WorkspacePath : WorkspacePath;
        var contactUsername = selected?.Username;
        var direction = ParseDirection();
        var from = VoiceQueryBuilder.ParseUtc(FromText, "from");
        var to = VoiceQueryBuilder.ParseUtc(ToText, "to");
        var maximumResults = ParseMaximumResults();
        var deepScan = DeepScan;
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
                    MaximumResults: maximumResults, DeepScan: deepScan),
                context,
                cancellationToken).ConfigureAwait(false);
        },
        result =>
        {
            Services.Project.Scan = result;
            Services.Project.Workspace = result.Workspace;
            var report = result.Report;
            var contact = Services.Project.SelectedContact!;
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
            AccountSummary = $"账号：{result.Workspace.DataSet.AccountId ?? "（未绑定）"}";
            ScanSummary = $"扫描完成：可导出 {report.ExportableVoiceCount} 条 / 匹配 {report.MatchedVoiceCount} 条；Missing {MissingCount} / Empty {EmptyCount} / InvalidHeader {InvalidHeaderCount} / Ambiguous {AmbiguousCount}；重复统计：{(deepScan ? DuplicateCount.ToString() : "未执行")}";
        });
    }

    private VoiceDirection ParseDirection()
    {
        return Enum.TryParse<VoiceDirection>(DirectionText, true, out var direction)
            ? direction
            : throw new AppFailureException(ErrorCode.InvalidRequest, "Direction must be incoming or outgoing.");
    }

    partial void OnDirectionTextChanged(string? value) => Services.Project.SelectionPlan = null;
    partial void OnFromTextChanged(string? value) => Services.Project.SelectionPlan = null;
    partial void OnToTextChanged(string? value) => Services.Project.SelectionPlan = null;
    partial void OnDeepScanChanged(bool value) => Services.Project.SelectionPlan = null;
    partial void OnMaximumResultsTextChanged(string? value) => Services.Project.SelectionPlan = null;
    private int? ParseMaximumResults() => string.IsNullOrWhiteSpace(MaximumResultsText) ? null : int.TryParse(MaximumResultsText, out var value) && value > 0 ? value : throw new AppFailureException(ErrorCode.InvalidRequest, "Maximum results must be a positive integer.");
}
