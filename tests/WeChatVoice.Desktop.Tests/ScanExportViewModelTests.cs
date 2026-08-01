using WeChatVoice.Desktop.Infrastructure;
using WeChatVoice.Desktop.ViewModels;
using WeChatVoice.Core.Errors;
using WeChatVoice.Workflows.Composition;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Desktop.Tests;

/// <summary>
/// Scan and export page tests against fake workflows: payload-state counts,
/// export entry/failure counts, and the raw-SILK-only surface (no decode
/// option is exposed by the ViewModel).
/// </summary>
public sealed class ScanExportViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WeChatVoiceToolkit.DesktopTests", Guid.NewGuid().ToString("N"));
    private readonly FakeScanWorkflow _scan = new();
    private readonly FakeExportWorkflow _export = new();

    public ScanExportViewModelTests()
    {
        var root = new WorkflowCompositionRoot(
            new TestDoubles.SilentConfirmation(),
            voiceScan: _scan,
            voiceExport: _export);
        Services = new DesktopServices(root, new DesktopLog(_root), new RecentWorkspaceStore(_root));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private DesktopServices Services { get; }

    [Fact]
    public async Task Scan_reports_all_four_payload_states()
    {
        var viewModel = new ScanViewModel(Services, marshal: action => action());
        viewModel.WorkspacePath = "C:\\workspace.json";
        viewModel.ContactUsername = "wxid_peer";

        await viewModel.ScanCommand.ExecuteAsync(null);

        Assert.Equal(4, viewModel.MatchedVoiceCount);
        Assert.Equal(1, viewModel.MissingCount);
        Assert.Equal(1, viewModel.EmptyCount);
        Assert.Equal(1, viewModel.InvalidHeaderCount);
        Assert.Equal(1, viewModel.AmbiguousCount);
        Assert.Equal(WorkflowState.Completed, viewModel.RunHost.State);
    }

    [Fact]
    public async Task Scan_without_workspace_fails_with_a_clear_parameter_error()
    {
        var viewModel = new ScanViewModel(Services, marshal: action => action());
        await viewModel.ScanCommand.ExecuteAsync(null);

        Assert.Equal(WorkflowState.Failed, viewModel.RunHost.State);
        Assert.Equal(ErrorCode.InvalidRequest, viewModel.RunHost.LastErrorCode);
    }

    [Fact]
    public async Task Export_reports_entries_and_partial_failures()
    {
        var viewModel = new ExportViewModel(Services, marshal: action => action());
        viewModel.WorkspacePath = "C:\\workspace.json";
        viewModel.OutputDirectory = "C:\\exports";
        viewModel.ContactUsername = "wxid_peer";

        await viewModel.ExportCommand.ExecuteAsync(null);

        Assert.Equal(1, viewModel.ExportedCount);
        Assert.Equal(0, viewModel.SkippedCount);
        Assert.Equal(1, viewModel.FailureCount);
        Assert.Single(viewModel.Failures);
        Assert.Contains("含失败", viewModel.ExportSummary, StringComparison.Ordinal);
        Assert.Equal(WorkflowState.Completed, viewModel.RunHost.State);
    }

    [Fact]
    public void Export_view_model_exposes_no_decode_option()
    {
        // The first usable chain is raw SILK only; the page must not offer WAV
        // decoding. There is no property on the ViewModel to toggle it.
        var properties = typeof(ExportViewModel).GetProperties().Select(static p => p.Name).ToArray();
        Assert.DoesNotContain(properties, name => name.Contains("Wav", StringComparison.OrdinalIgnoreCase) || name.Contains("Decode", StringComparison.OrdinalIgnoreCase));
    }
}
