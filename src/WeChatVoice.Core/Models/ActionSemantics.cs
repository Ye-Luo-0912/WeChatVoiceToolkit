namespace WeChatVoice.Core.Models;

/// <summary>
/// Content-fingerprint of a single "refresh" action for the Desktop home page.
/// The five actions share a common prefix but each has a distinct scope: what
/// it reuses, what it redoes, and what it never touches. Hosts render these as
/// separate actions so users never confuse "continue" with "re-run everything".
/// This model is presentation-agnostic; <see cref="Target"/> is a stable key
/// string that the host maps to its own page/navigation.
/// </summary>
public sealed record RefreshAction(
    string Id,
    string Title,
    string Description,
    string Preserves,
    string Redoes,
    string Target)
{
    /// <summary>Stable navigation target key understood by the host.</summary>
    public string Target { get; } = Target;
}

/// <summary>
/// The five distinct refresh semantics the UI must keep separate. Each entry
/// documents exactly what it may and may not redo, so the user is never asked
/// to "re-run the whole flow" for a small change.
/// </summary>
public static class RefreshActionCatalog
{
    public const string ContinueId = "continue";
    public const string RefreshSourceId = "refresh-source";
    public const string ReScanId = "rescan";
    public const string ReAnalyzeId = "reanalyze";
    public const string RebuildDatasetId = "rebuild-dataset";

    /// <summary>Continue: reuse every valid state; produce no new disk data.</summary>
    public static RefreshAction Continue() => new(
        ContinueId,
        "继续现有项目",
        "尽可能复用一切已验证状态，不重新执行快照、解密或 UAC。",
        "已复用的：已验证快照、已解密工作区、已扫描结果、已导出 SILK。",
        "不重新执行：快照、materialization、UAC、SILK 导出。",
        Target: ContinueId);

    /// <summary>Refresh from the Weixin source: re-check source, snapshot only if needed.</summary>
    public static RefreshAction RefreshFromSource() => new(
        RefreshSourceId,
        "从微信数据源刷新",
        "重新检查微信源是否变化，仅在必要时创建新快照。",
        "已复用的：未变化的已验证快照与工作区。",
        "重新执行：检测源变化；必要时新快照与 materialization。",
        Target: RefreshSourceId);

    /// <summary>Re-scan the current workspace: no new snapshot or materialization.</summary>
    public static RefreshAction ReScan() => new(
        ReScanId,
        "重新扫描当前工作区",
        "在当前已解密工作区上重新查询语音，不重新做快照或解密。",
        "已复用的：快照、materialization、账户确认。",
        "重新执行：语音查询与扫描（可应用新筛选）。",
        Target: ReScanId);

    /// <summary>Re-analyze audio: never re-export SILK.</summary>
    public static RefreshAction ReAnalyze() => new(
        ReAnalyzeId,
        "重新分析音频（时长 / 质量）",
        "仅对未知或过期的音频重新推算时长与质量，不重新导出 SILK。",
        "已复用的：已导出 SILK 与已缓存的时长/质量结果。",
        "重新执行：未知/过期音频的时长与质量分析。",
        Target: ReAnalyzeId);

    /// <summary>Rebuild the training dataset: never modifies raw export.</summary>
    public static RefreshAction RebuildDataset() => new(
        RebuildDatasetId,
        "重建训练数据集",
        "基于现有导出重新构建或更新训练数据集，不修改原始导出。",
        "已复用的：原始 SILK 导出与选择 profile。",
        "重新执行：数据集构建（SILK→WAV 派生产物）。",
        Target: RebuildDatasetId);

    /// <summary>All five actions in a stable, user-facing order.</summary>
    public static IReadOnlyList<RefreshAction> All { get; } =
    [
        Continue(),
        RefreshFromSource(),
        ReScan(),
        ReAnalyze(),
        RebuildDataset(),
    ];
}
