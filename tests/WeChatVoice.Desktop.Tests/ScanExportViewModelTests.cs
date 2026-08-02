using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Desktop.Infrastructure;
using WeChatVoice.Desktop.ViewModels;
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
    private readonly FakeContactWorkflow _contacts = new();

    public ScanExportViewModelTests()
    {
        var root = new WorkflowCompositionRoot(
            new TestDoubles.SilentConfirmation(),
            contactDiscovery: _contacts,
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
        Services.Project.SelectedContact = new ContactRecord("contact-1", "wxid_peer", "Peer", null);

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
        Services.Project.SelectedContact = new ContactRecord("contact-1", "wxid_peer", "Peer", null);
        var scan = new ScanViewModel(Services, marshal: action => action()) { WorkspacePath = "C:\\workspace.json" };
        await scan.ScanCommand.ExecuteAsync(null);

        await viewModel.ExportCommand.ExecuteAsync(null);

        Assert.Equal(1, viewModel.ExportedCount);
        Assert.Equal(0, viewModel.SkippedCount);
        Assert.Equal(1, viewModel.FailureCount);
        Assert.Single(viewModel.Failures);
        Assert.Contains("含失败", viewModel.ExportSummary, StringComparison.Ordinal);
        Assert.Equal(WorkflowState.Completed, viewModel.RunHost.State);
    }

    [Fact]
    public async Task Guided_flow_uses_the_second_explicit_contact_and_invalidates_the_plan_on_change()
    {
        var contacts = new ContactViewModel(Services, action => action()) { WorkspacePath = "C:\\workspace.json" };
        await contacts.LoadContactsCommand.ExecuteAsync(null);
        Assert.Null(contacts.SelectedContact);

        contacts.SelectedContact = contacts.Contacts[1];
        var scan = new ScanViewModel(Services, action => action()) { WorkspacePath = "C:\\workspace.json" };
        await scan.ScanCommand.ExecuteAsync(null);
        Assert.Equal("contact-b", Services.Project.SelectionPlan?.ContactId);

        contacts.SelectedContact = contacts.Contacts[0];
        Assert.Null(Services.Project.SelectionPlan);
        Assert.Null(Services.Project.Scan);

        contacts.SelectedContact = contacts.Contacts[1];
        await scan.ScanCommand.ExecuteAsync(null);
        var export = new ExportViewModel(Services, action => action())
        {
            WorkspacePath = "C:\\workspace.json",
            OutputDirectory = "C:\\exports",
        };
        await export.ExportCommand.ExecuteAsync(null);

        Assert.Equal(WorkflowState.Completed, export.RunHost.State);
        Assert.Equal(1, export.ExportedCount);
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
