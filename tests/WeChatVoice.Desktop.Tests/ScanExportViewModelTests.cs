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

    private static Task DirectInvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Scan_reports_all_four_payload_states()
    {
        var viewModel = new ScanViewModel(Services, DirectInvokeAsync);
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
        var viewModel = new ScanViewModel(Services, DirectInvokeAsync);
        await viewModel.ScanCommand.ExecuteAsync(null);

        Assert.Equal(WorkflowState.Failed, viewModel.RunHost.State);
        Assert.Equal(ErrorCode.InvalidRequest, viewModel.RunHost.LastErrorCode);
    }

    [Fact]
    public async Task Scan_input_parse_failures_are_reported_as_typed_errors()
    {
        var viewModel = new ScanViewModel(Services, DirectInvokeAsync)
        {
            WorkspacePath = "C:\\workspace.json",
            MaximumResultsText = "not-a-number",
        };
        Services.Project.SelectedContact = new ContactRecord("contact-1", "wxid_peer", "Peer", null);

        await viewModel.ScanCommand.ExecuteAsync(null);

        Assert.Equal(WorkflowState.Failed, viewModel.RunHost.State);
        Assert.Equal(ErrorCode.InvalidRequest, viewModel.RunHost.LastErrorCode);
        Assert.Null(_scan.LastRequest);
    }

    [Fact]
    public async Task Export_reports_entries_and_partial_failures()
    {
        var viewModel = new ExportViewModel(Services, DirectInvokeAsync);
        viewModel.WorkspacePath = "C:\\workspace.json";
        viewModel.OutputDirectory = "C:\\exports";
        Services.Project.SelectedContact = new ContactRecord("contact-1", "wxid_peer", "Peer", null);
        var scan = new ScanViewModel(Services, DirectInvokeAsync)
        {
            WorkspacePath = "C:\\workspace.json",
            MaximumResultsText = "1",
        };
        await scan.ScanCommand.ExecuteAsync(null);

        await viewModel.ExportCommand.ExecuteAsync(null);

        Assert.Equal(1, _scan.LastRequest?.MaximumResults);
        Assert.Equal(1, _export.LastRequest?.MaximumResults);
        Assert.Equal(1, viewModel.ExportedCount);
        Assert.Equal(0, viewModel.SkippedCount);
        Assert.Equal(1, viewModel.FailureCount);
        Assert.Single(viewModel.Failures);
        Assert.Contains("含失败", viewModel.ExportSummary, StringComparison.Ordinal);
        Assert.Equal(WorkflowState.Completed, viewModel.RunHost.State);
    }

    [Fact]
    public async Task Recover_without_a_journal_uses_typed_run_host_error()
    {
        var viewModel = new ExportViewModel(Services, DirectInvokeAsync);

        await viewModel.RecoverCommand.ExecuteAsync(null);

        Assert.Equal(WorkflowState.Failed, viewModel.RunHost.State);
        Assert.Equal(ErrorCode.InvalidRequest, viewModel.RunHost.LastErrorCode);
    }

    [Fact]
    public async Task Guided_flow_uses_the_second_explicit_contact_and_invalidates_the_plan_on_change()
    {
        var contacts = new ContactViewModel(Services, DirectInvokeAsync) { WorkspacePath = "C:\\workspace.json" };
        await contacts.LoadContactsCommand.ExecuteAsync(null);
        Assert.Null(contacts.SelectedContact);

        contacts.SelectedContact = contacts.Contacts[1];
        var scan = new ScanViewModel(Services, DirectInvokeAsync)
        {
            WorkspacePath = "C:\\workspace.json",
            MaximumResultsText = "1",
        };
        await scan.ScanCommand.ExecuteAsync(null);
        Assert.Equal("contact-b", Services.Project.SelectionPlan?.ContactId);

        contacts.SelectedContact = contacts.Contacts[0];
        Assert.Null(Services.Project.SelectionPlan);
        Assert.Null(Services.Project.Scan);

        contacts.SelectedContact = contacts.Contacts[1];
        await scan.ScanCommand.ExecuteAsync(null);
        Assert.Equal(Path.GetFullPath("C:\\workspace.json"), Services.Project.WorkspacePath);
        Assert.Equal(Path.GetFullPath("C:\\workspace.json"), _contacts.LastRequest?.WorkspacePath);
        Assert.Equal(Path.GetFullPath("C:\\workspace.json"), _scan.LastRequest?.WorkspacePath);
        var export = new ExportViewModel(Services, DirectInvokeAsync)
        {
            WorkspacePath = "C:\\workspace.json",
            OutputDirectory = "C:\\exports",
        };
        await export.ExportCommand.ExecuteAsync(null);

        Assert.Equal(WorkflowState.Completed, export.RunHost.State);
        Assert.Equal(1, export.ExportedCount);
        Assert.Equal(Path.GetFullPath("C:\\workspace.json"), _export.LastRequest?.WorkspacePath);
        Assert.Equal(_scan.LastRequest?.MaximumResults, _export.LastRequest?.MaximumResults);
        Assert.Equal(_scan.Result.Report.ResultSetFingerprint, _export.LastRequest?.ExpectedResultSetFingerprint);
        Assert.Contains("Remark B", export.ContactSelectionSummary, StringComparison.Ordinal);
        Assert.Contains("wxid_b", export.ContactSelectionSummary, StringComparison.Ordinal);
        Assert.Contains("incoming", export.SelectionPlanSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Scan_result_is_discarded_when_contact_changes_during_run()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _scan.RunOverride = async cancellationToken =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return _scan.Result;
        };

        var firstContact = new ContactRecord("contact-a", "wxid_a", "A", "Remark A");
        Services.Project.SelectedContact = firstContact;
        var viewModel = new ScanViewModel(Services, DirectInvokeAsync) { WorkspacePath = "C:\\workspace.json" };
        var run = viewModel.ScanCommand.ExecuteAsync(null);

        await started.Task;
        Services.Project.SelectedContact = new ContactRecord("contact-b", "wxid_b", "B", "Remark B");
        release.SetResult();
        await run;

        Assert.Equal(WorkflowState.Failed, viewModel.RunHost.State);
        Assert.Equal(ErrorCode.InvalidRequest, viewModel.RunHost.LastErrorCode);
        Assert.Null(Services.Project.Scan);
    }

    [Fact]
    public async Task Navigation_requires_contact_then_exportable_scan()
    {
        Services.Project.Workspace = TestDoubles.Verified();
        var scan = new ScanViewModel(Services, DirectInvokeAsync);
        var export = new ExportViewModel(Services, DirectInvokeAsync);

        Assert.False(scan.CanNavigate);
        Assert.False(export.CanNavigate);

        Services.Project.SelectedContact = new ContactRecord("contact-a", "wxid_a", "A", "Remark A");
        Assert.True(scan.CanNavigate);
        Assert.False(export.CanNavigate);

        scan.WorkspacePath = "C:\\workspace.json";
        await scan.ScanCommand.ExecuteAsync(null);

        Assert.True(export.CanNavigate);
    }

    [Fact]
    public void Export_view_model_exposes_no_decode_option()
    {
        // The first usable chain is raw SILK only; the page must not offer WAV
        // decoding. There is no property on the ViewModel to toggle it.
        var properties = typeof(ExportViewModel).GetProperties().Select(static p => p.Name).ToArray();
        Assert.DoesNotContain(properties, name => name.Contains("Wav", StringComparison.OrdinalIgnoreCase) || name.Contains("Decode", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("FromText", properties);
        Assert.DoesNotContain("ToText", properties);
    }
}
