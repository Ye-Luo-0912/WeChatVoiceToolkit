using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Desktop.Infrastructure;
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
        WorkspacePath = services.Project.WorkspacePath;
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
    [ObservableProperty] private string? _minimumDurationMsText;
    [ObservableProperty] private string? _maximumDurationMsText;
    [ObservableProperty] private string? _minimumPayloadBytesText;
    [ObservableProperty] private string? _maximumPayloadBytesText;

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
        var sessionWorkspacePath = Services.Project.WorkspacePath;
        var contactUsername = selected?.Username;
        var directionText = DirectionText;
        var fromText = FromText;
        var toText = ToText;
        var maximumResultsText = MaximumResultsText;
        var minimumDurationMsText = MinimumDurationMsText;
        var maximumDurationMsText = MaximumDurationMsText;
        var minimumPayloadBytesText = MinimumPayloadBytesText;
        var maximumPayloadBytesText = MaximumPayloadBytesText;
        var deepScan = DeepScan;
        var resolveDurations = ResolveDurations;
        ScanParameters? parsedParameters = null;
        return RunHost.RunAsync<VoiceScanWorkflowResult>(
        async (context, cancellationToken) =>
        {
            // Keep all input parsing inside the WorkflowRunHost boundary so
            // invalid UI text becomes a typed LastErrorCode instead of an
            // unobserved command exception.
            var direction = ParseDirection(directionText);
            var from = VoiceQueryBuilder.ParseUtc(fromText, "from");
            var to = VoiceQueryBuilder.ParseUtc(toText, "to");
            var maximumResults = ParseMaximumResults(maximumResultsText);
            var minimumDurationMs = ParseNonNegativeLong(minimumDurationMsText, "minimum duration");
            var maximumDurationMs = ParseNonNegativeLong(maximumDurationMsText, "maximum duration");
            var minimumPayloadBytes = ParseNonNegativeLong(minimumPayloadBytesText, "minimum payload size");
            var maximumPayloadBytes = ParseNonNegativeLong(maximumPayloadBytesText, "maximum payload size");
            ValidateRange(minimumDurationMs, maximumDurationMs, "duration");
            ValidateRange(minimumPayloadBytes, maximumPayloadBytes, "payload size");
            parsedParameters = new ScanParameters(
                direction,
                from,
                to,
                maximumResults,
                minimumDurationMs,
                maximumDurationMs,
                minimumPayloadBytes,
                maximumPayloadBytes);
            if (selected is null || string.IsNullOrWhiteSpace(selected.Username))
            {
                throw new AppFailureException(ErrorCode.InvalidRequest, "Please select a contact before scanning.");
            }

            if (string.IsNullOrWhiteSpace(workspacePath))
            {
                throw new AppFailureException(ErrorCode.InvalidRequest, "Workspace path is required.");
            }

            if (!string.IsNullOrWhiteSpace(sessionWorkspacePath)
                && !PathsEqual(workspacePath, sessionWorkspacePath))
            {
                throw new AppFailureException(ErrorCode.InvalidRequest, "Workspace path changed; load the selected Workspace before scanning.");
            }

            return await Workflows.VoiceScan.RunAsync(
                new VoiceScanWorkflowRequest(
                    workspacePath,
                    ContactUsername: contactUsername,
                    ConversationId: null,
                    Direction: direction,
                    From: from,
                    To: to,
                    MaximumResults: maximumResults,
                    DeepScan: deepScan,
                    ResolveDurations: resolveDurations,
                    ExpectedContactId: selected.ContactId,
                    MinimumDurationMs: minimumDurationMs,
                    MaximumDurationMs: maximumDurationMs,
                    MinimumPayloadBytes: minimumPayloadBytes,
                    MaximumPayloadBytes: maximumPayloadBytes),
                context,
                cancellationToken).ConfigureAwait(false);
        },
        result =>
        {
            if (!IsCurrentRequest(
                selected,
                workspacePath,
                directionText,
                fromText,
                toText,
                maximumResultsText,
                minimumDurationMsText,
                maximumDurationMsText,
                minimumPayloadBytesText,
                maximumPayloadBytesText,
                deepScan,
                resolveDurations))
            {
                throw new AppFailureException(ErrorCode.InvalidRequest, "扫描期间联系人或查询参数发生变化；请重新扫描当前选择。");
            }

            Services.Project.Scan = result;
            Services.Project.Workspace = result.Workspace;
            var report = result.Report;
            var contact = selected!;
            var parameters = parsedParameters
                ?? throw new AppFailureException(ErrorCode.WorkflowFailed, "扫描请求参数未能固定。");
            Services.Project.SelectionPlan = result.Selection
                ?? throw new AppFailureException(ErrorCode.WorkflowFailed, "扫描未返回不可变的导出选择计划。");
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
        if (!Enum.TryParse<VoiceDirection>(directionText, true, out var direction))
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, "Desktop voice scanning requires incoming direction.");
        }

        return direction == VoiceDirection.Incoming
            ? direction
            : throw new AppFailureException(ErrorCode.InvalidRequest, "Desktop voice scanning is fixed to incoming direction.");
    }

    partial void OnWorkspacePathChanged(string? value) => InvalidateSelection();
    partial void OnDirectionTextChanged(string? value) => InvalidateSelection();
    partial void OnFromTextChanged(string? value) => InvalidateSelection();
    partial void OnToTextChanged(string? value) => InvalidateSelection();
    partial void OnDeepScanChanged(bool value) => InvalidateSelection();
    partial void OnMaximumResultsTextChanged(string? value) => InvalidateSelection();
    partial void OnMinimumDurationMsTextChanged(string? value) => InvalidateSelection();
    partial void OnMaximumDurationMsTextChanged(string? value) => InvalidateSelection();
    partial void OnMinimumPayloadBytesTextChanged(string? value) => InvalidateSelection();
    partial void OnMaximumPayloadBytesTextChanged(string? value) => InvalidateSelection();
    partial void OnResolveDurationsChanged(bool value) => InvalidateSelection();

    private void InvalidateSelection()
    {
        Services.Project.SelectionPlan = null;
        Services.Project.Scan = null;
        Services.Project.LastExportRun = null;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
    private static int? ParseMaximumResults(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : int.TryParse(value, out var parsed) && parsed > 0
                ? parsed
                : throw new AppFailureException(ErrorCode.InvalidRequest, "Maximum results must be a positive integer.");

    private static long? ParseNonNegativeLong(string? value, string label)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : long.TryParse(value, out var parsed) && parsed >= 0
                ? parsed
                : throw new AppFailureException(ErrorCode.InvalidRequest, $"{label} must be a non-negative integer.");

    private static void ValidateRange(long? minimum, long? maximum, string label)
    {
        if (minimum is not null && maximum is not null && minimum > maximum)
        {
            throw new AppFailureException(ErrorCode.InvalidRequest, $"Minimum {label} cannot exceed maximum {label}.");
        }
    }

    private bool IsCurrentRequest(
        ContactRecord? selected,
        string? workspacePath,
        string? directionText,
        string? fromText,
        string? toText,
        string? maximumResultsText,
        string? minimumDurationMsText,
        string? maximumDurationMsText,
        string? minimumPayloadBytesText,
        string? maximumPayloadBytesText,
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
            && string.Equals(MinimumDurationMsText, minimumDurationMsText, StringComparison.Ordinal)
            && string.Equals(MaximumDurationMsText, maximumDurationMsText, StringComparison.Ordinal)
            && string.Equals(MinimumPayloadBytesText, minimumPayloadBytesText, StringComparison.Ordinal)
            && string.Equals(MaximumPayloadBytesText, maximumPayloadBytesText, StringComparison.Ordinal)
            && DeepScan == deepScan
            && ResolveDurations == resolveDurations;

    private sealed record ScanParameters(
        VoiceDirection Direction,
        DateTimeOffset? From,
        DateTimeOffset? To,
        int? MaximumResults,
        long? MinimumDurationMs,
        long? MaximumDurationMs,
        long? MinimumPayloadBytes,
        long? MaximumPayloadBytes);

    protected override void OnProjectPropertyChanged(string? propertyName)
    {
        if (propertyName == nameof(ExportProjectSession.WorkspacePath))
        {
            WorkspacePath = Services.Project.WorkspacePath;
        }
    }
}
