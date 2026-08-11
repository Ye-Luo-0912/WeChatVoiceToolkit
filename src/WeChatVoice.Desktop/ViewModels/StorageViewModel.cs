using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeChatVoice.Core.Models;
using WeChatVoice.Desktop.Infrastructure;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Desktop.ViewModels;

/// <summary>
/// Storage management page: a read-only inventory of application-owned storage
/// and an explicit, previewed cleanup of only transient and expired-recoverable
/// objects. User assets (raw SILK exports, curated datasets) and verified
/// reusable workspaces are never auto-deleted here.
/// </summary>
public sealed partial class StorageViewModel : PageViewModelBase
{
    public StorageViewModel(DesktopServices services)
        : base(services)
    {
    }

    public override string Title => "存储管理";

    [ObservableProperty]
    private StorageInventorySummary? _inventory;

    [ObservableProperty]
    private IReadOnlyList<StorageAssetRecord> _assets = [];

    [ObservableProperty]
    private string _summaryText = "本页只显示应用自有存储的占用与可回收对象；原始 SILK 导出与数据集不会被自动删除。";

    [ObservableProperty]
    private string? _cleanupSummary;

    private StorageCleanupPreview? _cleanupPreview;
    private bool _cleanupArmed;

    public override async Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        await base.OnNavigatedToAsync(cancellationToken).ConfigureAwait(false);
        if (Inventory is null)
        {
            await RefreshInventoryAsync().ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private Task RefreshInventoryAsync()
    {
        _cleanupArmed = false;
        _cleanupPreview = null;
        return RunHost.RunAsync(
            async (context, cancellationToken) => await Workflows.StorageLifecycle.InventoryAsync(
                new StorageInventoryRequest(),
                context,
                cancellationToken).ConfigureAwait(false),
            summary =>
            {
                Inventory = summary;
                Assets = summary.Assets;
                SummaryText = FormatSummary(summary);
            });
    }

    [RelayCommand]
    private Task PreviewCleanupAsync()
    {
        _cleanupArmed = false;
        _cleanupPreview = null;
        return RunHost.RunAsync(
            async (context, cancellationToken) => await Workflows.StorageLifecycle.PreviewCleanupAsync(
                new StorageCleanupRequest(),
                context,
                cancellationToken).ConfigureAwait(false),
            preview =>
            {
                _cleanupPreview = preview;
                _cleanupArmed = true;
                CleanupSummary = $"预检将回收 {preview.ItemCount} 个临时/可恢复对象，共 {preview.TotalBytes} bytes。再次点击“执行清理”确认；验证过的 Workspace、原始导出与数据集不会被删除。";
            });
    }

    [RelayCommand]
    private Task CleanupAsync()
    {
        if (!_cleanupArmed || _cleanupPreview is null)
        {
            CleanupSummary = "请先执行清理预检，并确认预检结果。";
            return Task.CompletedTask;
        }

        _cleanupArmed = false;
        return RunHost.RunAsync(
            async (context, cancellationToken) => await Workflows.StorageLifecycle.CleanupAsync(
                new StorageCleanupRequest(),
                context,
                cancellationToken).ConfigureAwait(false),
            result =>
            {
                CleanupSummary = $"已回收 {result.DeletedCount} 个对象，共 {result.DeletedBytes} bytes；跳过 {result.SkippedReasons.Count} 项。";
                _cleanupPreview = null;
                Inventory = null;
                Assets = [];
                SummaryText = "清理完成，已重新扫描可用的占用数据。";
                _ = RefreshInventoryAsync();
            });
    }

    private static string FormatSummary(StorageInventorySummary summary) =>
        $"快照 {FormatBytes(summary.SnapshotBytes)} · 明文 Workspace {FormatBytes(summary.WorkspaceBytes)} · "
        + $"导出 {FormatBytes(summary.ExportBytes)} · 数据集 {FormatBytes(summary.DatasetBytes)} · "
        + $"临时/可恢复 {FormatBytes(summary.TempBytes + summary.RecoverableBytes)} · "
        + $"可安全回收约 {FormatBytes(summary.SafelyReclaimableBytes)}";

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    public static string KindLabel(StorageAssetKind kind) => kind switch
    {
        StorageAssetKind.Transient => "临时",
        StorageAssetKind.RecoverableIntermediate => "可恢复",
        StorageAssetKind.ReusableIntermediate => "可复用",
        StorageAssetKind.UserAsset => "用户资产",
        StorageAssetKind.DerivedUserAsset => "派生数据集",
        _ => "未知",
    };
}