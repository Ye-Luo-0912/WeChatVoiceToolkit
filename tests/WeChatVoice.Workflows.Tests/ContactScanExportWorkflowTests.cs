using WeChatVoice.Application;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Workflows.Workflows;
using WeChatVoice.Workflows.Workspaces;

namespace WeChatVoice.Workflows.Tests;

/// <summary>
/// Workflow orchestration tests against the in-process Fake Backend. No real
/// Weixin process, SQLite database, or process-memory access is involved.
/// </summary>
public sealed class ContactScanExportWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WeChatVoiceToolkit.WorkflowTests", Guid.NewGuid().ToString("N"));
    private readonly string _workspacePath;
    private readonly FakeBackend _backend = new();

    public ContactScanExportWorkflowTests()
    {
        var workspace = TestFixtures.MakeWorkspace();
        _workspacePath = TestFixtures.WriteWorkspaceFile(Path.Combine(_root, "workspace"), workspace);
        var verifier = new TestFixtures.FakeWorkspaceVerifier(TestFixtures.Verified(workspace));
        _opener = new VoiceCatalogOpener(
            new WorkspaceLoader(verifier),
            new DataSetAdapterResolver([_backend.Adapter]));
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

    private readonly VoiceCatalogOpener _opener;

    [Fact]
    public async Task Contact_discovery_returns_the_exact_one_to_one_contact()
    {
        var workflow = new ContactDiscoveryWorkflow(_opener);
        var context = new WorkflowContext(new TestConfirmation());
        var result = await workflow.RunAsync(
            new ContactDiscoveryRequest(_workspacePath, Username: FakeBackend.ContactUsername),
            context,
            CancellationToken.None);

        var contact = Assert.Single(result.Contacts);
        Assert.Equal(FakeBackend.ContactUsername, contact.Username);
        Assert.Equal(FakeBackend.ContactUsername, contact.ConversationId);
        Assert.Equal(WorkflowState.Completed, context.StateMachine.State);
    }

    [Fact]
    public async Task Scan_reports_payload_states_explicitly()
    {
        _backend.Fill(
            FakeBackend.Linked("m1", 1_700_000_000, VoiceDirection.Incoming),
            FakeBackend.Linked("m2", 1_700_000_060, VoiceDirection.Incoming),
            FakeBackend.Broken("m3", 1_700_000_120, VoiceDirection.Incoming, VoicePayloadState.Missing),
            FakeBackend.Broken("m4", 1_700_000_180, VoiceDirection.Incoming, VoicePayloadState.Empty),
            FakeBackend.Broken("m5", 1_700_000_240, VoiceDirection.Incoming, VoicePayloadState.InvalidHeader),
            FakeBackend.Broken("m6", 1_700_000_300, VoiceDirection.Incoming, VoicePayloadState.Ambiguous));

        var workflow = new VoiceScanWorkflow(_opener, new ContactResolver());
        var context = new WorkflowContext(new TestConfirmation());
        var result = await workflow.RunAsync(
            new VoiceScanWorkflowRequest(_workspacePath, FakeBackend.ContactUsername),
            context,
            CancellationToken.None);

        Assert.Equal(6, result.Report.MatchedVoiceCount);
        Assert.Equal(1, result.Report.UnassociatedMediaCount);
        Assert.Equal(1, result.Report.EmptyBlobCount);
        Assert.Equal(1, result.Report.InvalidHeaderCount);
        Assert.Equal(1, result.Report.AmbiguousPayloadCount);
    }

    [Fact]
    public async Task Compatibility_export_consumes_only_scan_eligible_voices()
    {
        _backend.Fill(
            FakeBackend.Linked("m1", 1_700_000_000, VoiceDirection.Incoming),
            FakeBackend.Linked("m2", 1_700_000_060, VoiceDirection.Incoming),
            FakeBackend.Broken("m3", 1_700_000_120, VoiceDirection.Incoming, VoicePayloadState.InvalidHeader),
            FakeBackend.Broken("m4", 1_700_000_180, VoiceDirection.Incoming, VoicePayloadState.Missing));
        var outputRoot = Path.Combine(_root, "exports");
        var workflow = new VoiceExportWorkflow(_opener, new ContactResolver());
        var context = new WorkflowContext(new TestConfirmation());

        var result = await workflow.RunAsync(
            new VoiceExportWorkflowRequest(_workspacePath, outputRoot, FakeBackend.ContactUsername),
            context,
            CancellationToken.None);

        Assert.Equal(2, result.Manifest.Entries.Count);
        Assert.Empty(result.Manifest.Failures);
        Assert.Equal(ExportRunStatus.Completed, result.Manifest.RunStatus);
        Assert.True(Directory.Exists(outputRoot));
        Assert.True(Directory.Exists(Path.Combine(outputRoot, "runs")));
    }

    [Fact]
    public async Task Export_applies_the_global_maximum_after_eligibility()
    {
        _backend.Fill(
            FakeBackend.Linked("m1", 1_700_000_000, VoiceDirection.Incoming),
            FakeBackend.Linked("m2", 1_700_000_060, VoiceDirection.Incoming));
        var workflow = new VoiceExportWorkflow(_opener, new ContactResolver());
        var context = new WorkflowContext(new TestConfirmation());

        var result = await workflow.RunAsync(
            new VoiceExportWorkflowRequest(
                _workspacePath,
                Path.Combine(_root, "limited-export"),
                FakeBackend.ContactUsername,
                MaximumResults: 1),
            context,
            CancellationToken.None);

        Assert.Single(result.Manifest.Entries);
        // The catalog query deliberately has no SQL-level result limit here:
        // the verified scan must skip ineligible candidates before counting
        // MaximumResults. The immutable plan still carries the requested 1.
        Assert.Null(_backend.LastVoiceQuery?.MaximumResults);
    }

    [Fact]
    public async Task Prepared_scan_selection_is_the_formal_export_input_and_retains_workspace_document_path()
    {
        _backend.Fill(
            FakeBackend.Linked("m1", 1_700_000_000, VoiceDirection.Incoming),
            FakeBackend.Linked("m2", 1_700_000_060, VoiceDirection.Incoming),
            FakeBackend.Linked("m3", 1_700_000_120, VoiceDirection.Outgoing));

        var contactContext = new WorkflowContext(new TestConfirmation());
        var contact = await new ContactDiscoveryWorkflow(_opener).RunAsync(
            new ContactDiscoveryRequest(_workspacePath),
            contactContext,
            CancellationToken.None);
        var selectedContact = Assert.Single(contact.Contacts);

        var scanContext = new WorkflowContext(new TestConfirmation());
        var scan = await new VoiceScanWorkflow(_opener, new ContactResolver()).RunAsync(
            new VoiceScanWorkflowRequest(
                contact.WorkspaceDocumentPath,
                selectedContact.Username,
                ExpectedContactId: selectedContact.ContactId,
                MaximumResults: 1),
            scanContext,
            CancellationToken.None);

        Assert.NotNull(scan.Selection);
        var plan = scan.Selection!;
        Assert.Equal(Path.GetFullPath(_workspacePath), plan.WorkspaceDocumentPath);
        Assert.NotEqual(Path.GetFullPath(scan.Workspace.Workspace.SourceRoot), plan.WorkspaceDocumentPath);
        Assert.Equal(VoiceDirection.Incoming, plan.Direction);
        Assert.Equal(1, plan.MaximumResults);
        Assert.Equal(plan.ScanReport.ResultSetFingerprint, plan.ResultSetFingerprint);
        Assert.Equal(plan.ScanReport.ExportableVoiceCount, plan.ResultCount);

        var exportContext = new WorkflowContext(new TestConfirmation());
        var exported = await new VoiceExportWorkflow(_opener, new ContactResolver()).RunAsync(
            plan,
            new ExportDestination(Path.Combine(_root, "formal-export")),
            exportContext,
            CancellationToken.None);

        Assert.Single(exported.Manifest.Entries);
        Assert.Equal(VoiceDirection.Incoming, Assert.Single(exported.Manifest.Entries).Direction);
        // Formal export consumes the prepared records and does not issue a
        // second catalog query.
        Assert.Null(_backend.LastVoiceQuery?.MaximumResults);
        Assert.Equal(VoiceDirection.Incoming, _backend.LastVoiceQuery?.Direction);
        Assert.Equal(WorkflowState.Completed, exportContext.StateMachine.State);
    }

    [Fact]
    public async Task Formal_export_rejects_a_plan_bound_to_a_different_adapter_version()
    {
        _backend.Fill(FakeBackend.Linked("m1", 1_700_000_000, VoiceDirection.Incoming));
        var scan = await new VoiceScanWorkflow(_opener, new ContactResolver()).RunAsync(
            new VoiceScanWorkflowRequest(_workspacePath, FakeBackend.ContactUsername),
            new WorkflowContext(new TestConfirmation()),
            CancellationToken.None);
        Assert.NotNull(scan.Selection);
        var plan = scan.Selection!;
        var mismatched = new PreparedVoiceSelection(
            plan.WorkspaceDocumentPath,
            plan.WorkspaceId,
            plan.DatasetId,
            plan.AccountId,
            plan.SnapshotId,
            plan.AdapterId,
            "fake-v2",
            plan.ContactId,
            plan.ContactUsername,
            plan.Direction,
            plan.FromUtc,
            plan.ToUtc,
            plan.MaximumResults,
            plan.DeepScan,
            plan.ResolveDurations,
            plan.MinimumDurationMs,
            plan.MaximumDurationMs,
            plan.MinimumPayloadBytes,
            plan.MaximumPayloadBytes,
            plan.QueryFingerprint,
            plan.ResultSetFingerprint,
            plan.ResultCount,
            plan.TotalPayloadBytes,
            plan.SelectionEngineVersion,
            plan.DurationResolverVersion,
            plan.ScanReport);

        var context = new WorkflowContext(new TestConfirmation());
        var exception = await Assert.ThrowsAsync<AppFailureException>(() =>
            new VoiceExportWorkflow(_opener, new ContactResolver()).RunAsync(
                mismatched,
                new ExportDestination(Path.Combine(_root, "mismatched-export")),
                context,
                CancellationToken.None));

        Assert.Equal(ErrorCode.SelectionPlanMismatch, exception.Code);
        Assert.Equal(WorkflowState.Failed, context.StateMachine.State);
    }

    [Fact]
    public async Task Formal_export_uses_the_prepared_records_without_a_second_catalog_query()
    {
        _backend.Fill(FakeBackend.Linked("planned", 1_700_000_000, VoiceDirection.Incoming));
        var scan = await new VoiceScanWorkflow(_opener, new ContactResolver()).RunAsync(
            new VoiceScanWorkflowRequest(_workspacePath, FakeBackend.ContactUsername),
            new WorkflowContext(new TestConfirmation()),
            CancellationToken.None);
        Assert.NotNull(scan.Selection);
        Assert.Equal(1, _backend.QueryVoiceCount);

        _backend.Fill(FakeBackend.Linked("changed", 1_700_000_001, VoiceDirection.Incoming));
        var context = new WorkflowContext(new TestConfirmation());
        var result = await new VoiceExportWorkflow(_opener, new ContactResolver()).RunAsync(
            scan.Selection!,
            new ExportDestination(Path.Combine(_root, "changed-export")),
            context,
            CancellationToken.None);

        Assert.Equal(1, _backend.QueryVoiceCount);
        Assert.Equal("planned", Assert.Single(result.Manifest.Entries).MessageId);
        Assert.Equal(WorkflowState.Completed, context.StateMachine.State);
    }

    [Fact]
    public async Task Scan_rejects_a_contact_id_that_changed_after_selection()
    {
        var workflow = new VoiceScanWorkflow(_opener, new ContactResolver());
        var context = new WorkflowContext(new TestConfirmation());

        var exception = await Assert.ThrowsAsync<AppFailureException>(() => workflow.RunAsync(
            new VoiceScanWorkflowRequest(
                _workspacePath,
                FakeBackend.ContactUsername,
                ExpectedContactId: "contact-that-is-not-peer"),
            context,
            CancellationToken.None));

        Assert.Equal(ErrorCode.SelectionPlanMismatch, exception.Code);
        Assert.Equal(WorkflowState.Failed, context.StateMachine.State);
    }

    [Fact]
    public async Task Export_rejects_a_contact_id_that_changed_after_selection()
    {
        var workflow = new VoiceExportWorkflow(_opener, new ContactResolver());
        var context = new WorkflowContext(new TestConfirmation());

        var exception = await Assert.ThrowsAsync<AppFailureException>(() => workflow.RunAsync(
            new VoiceExportWorkflowRequest(
                _workspacePath,
                Path.Combine(_root, "contact-mismatch"),
                FakeBackend.ContactUsername,
                ExpectedContactId: "contact-that-is-not-peer"),
            context,
            CancellationToken.None));

        Assert.Equal(ErrorCode.SelectionPlanMismatch, exception.Code);
        Assert.Equal(WorkflowState.Failed, context.StateMachine.State);
    }

    [Fact]
    public async Task Export_cancellation_transitions_to_cancelled()
    {
        var backend = new FakeBackend(voicesFactory: token => AsyncThrowOn(token));
        var opener = OpenerFor(backend);
        var workflow = new VoiceExportWorkflow(opener, new ContactResolver());
        var context = new WorkflowContext(new TestConfirmation());
        using var cts = new CancellationTokenSource();

        var task = workflow.RunAsync(
            new VoiceExportWorkflowRequest(_workspacePath, Path.Combine(_root, "cancel"), FakeBackend.ContactUsername),
            context,
            cts.Token);
        await Task.Yield();
        cts.Cancel();

        // TaskCanceledException is a subclass of OperationCanceledException;
        // the exact concrete type is not part of the workflow contract.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(WorkflowState.Cancelled, context.StateMachine.State);
    }

    [Fact]
    public async Task Workflow_can_retry_after_a_failure()
    {
        var workflow = new VoiceExportWorkflow(_opener, new ContactResolver());
        _backend.Fill(FakeBackend.Linked("m1", 1_700_000_000, VoiceDirection.Incoming));

        // First run fails genuinely before export (workspace file missing);
        // the workflow transitions to Failed and the exception propagates.
        var missingPath = Path.Combine(_root, "workspace", "does-not-exist.json");
        var firstContext = new WorkflowContext(new TestConfirmation());
        await Assert.ThrowsAsync<FileNotFoundException>(() => workflow.RunAsync(
            new VoiceExportWorkflowRequest(missingPath, Path.Combine(_root, "retry"), FakeBackend.ContactUsername),
            firstContext,
            CancellationToken.None));
        Assert.Equal(WorkflowState.Failed, firstContext.StateMachine.State);

        // Retry with a valid workspace succeeds on a fresh run.
        var secondContext = new WorkflowContext(new TestConfirmation());
        var second = await workflow.RunAsync(
            new VoiceExportWorkflowRequest(_workspacePath, Path.Combine(_root, "retry"), FakeBackend.ContactUsername),
            secondContext,
            CancellationToken.None);

        Assert.Single(second.Manifest.Entries);
        Assert.Equal(WorkflowState.Completed, secondContext.StateMachine.State);
    }

    private VoiceCatalogOpener OpenerFor(FakeBackend backend)
    {
        var workspace = TestFixtures.MakeWorkspace();
        var verifier = new TestFixtures.FakeWorkspaceVerifier(TestFixtures.Verified(workspace));
        return new VoiceCatalogOpener(
            new WorkspaceLoader(verifier),
            new DataSetAdapterResolver([backend.Adapter]));
    }

    private static async IAsyncEnumerable<VoiceRecord> AsyncThrowOn([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return FakeBackend.Linked("m1", 1_700_000_000, VoiceDirection.Incoming);
        await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
        yield break;
    }

    private sealed class TestConfirmation : Core.Ports.IAccountConfirmation
    {
        public Task<Core.Models.AccountConfirmation> ConfirmAsync(Core.Models.AccountIdentityReport report, CancellationToken cancellationToken)
            => Task.FromResult(new Core.Models.AccountConfirmation(true, report.AccountCandidate));
    }
}
