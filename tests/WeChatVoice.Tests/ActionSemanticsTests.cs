using WeChatVoice.Core.Models;

namespace WeChatVoice.Tests;

public sealed class ActionSemanticsTests
{
    [Fact]
    public void Catalog_exposes_all_five_distinct_refresh_actions()
    {
        var actions = RefreshActionCatalog.All;

        Assert.Equal(5, actions.Count);
        Assert.Equal(5, actions.Select(static action => action.Id).Distinct().Count());
        Assert.Contains(actions, static action => action.Id == RefreshActionCatalog.ContinueId);
        Assert.Contains(actions, static action => action.Id == RefreshActionCatalog.RefreshSourceId);
        Assert.Contains(actions, static action => action.Id == RefreshActionCatalog.ReScanId);
        Assert.Contains(actions, static action => action.Id == RefreshActionCatalog.ReAnalyzeId);
        Assert.Contains(actions, static action => action.Id == RefreshActionCatalog.RebuildDatasetId);
    }

    [Fact]
    public void Each_action_documents_its_scope()
    {
        foreach (var action in RefreshActionCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(action.Title));
            Assert.False(string.IsNullOrWhiteSpace(action.Description));
            Assert.False(string.IsNullOrWhiteSpace(action.Redoes));
            Assert.False(string.IsNullOrWhiteSpace(action.Preserves));
            Assert.False(string.IsNullOrWhiteSpace(action.Target));
        }
    }

    [Fact]
    public void Rebuild_dataset_never_claims_to_modify_raw_export()
    {
        var rebuild = RefreshActionCatalog.RebuildDataset();
        Assert.Contains("不修改原始导出", rebuild.Description, StringComparison.Ordinal);
        Assert.Contains("原始 SILK 导出", rebuild.Preserves, StringComparison.Ordinal);
        Assert.DoesNotContain("SILK 导出", rebuild.Redoes, StringComparison.Ordinal);
    }

    [Fact]
    public void Continue_preserves_everything_and_does_not_redo_expensive_steps()
    {
        var continueAction = RefreshActionCatalog.Continue();
        Assert.Contains("不重新执行", continueAction.Redoes, StringComparison.Ordinal);
        Assert.Contains("快照", continueAction.Redoes, StringComparison.Ordinal);
        Assert.Contains("UAC", continueAction.Redoes, StringComparison.Ordinal);
    }
}