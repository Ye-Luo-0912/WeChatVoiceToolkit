using System.Buffers.Binary;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Desktop.Infrastructure;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Desktop.Tests;

/// <summary>
/// Fake workflows for ViewModel tests. ViewModels never touch a real Weixin
/// process, SQLite database, or Key Broker implementation.
/// </summary>
public sealed class FakeEnvironmentWorkflow : IEnvironmentAssessmentWorkflow
{
    public EnvironmentAssessmentResult Result { get; set; } = new(
        IsWindows: true,
        RunningWeChatProcesses: [],
        SupportedProcessNames: ["WeChat.exe"],
        KeyAcquisitionProfiles: [],
        MatchingKeyAcquisitionProfiles: [],
        RegisteredAdapters: [],
        AdapterMatchEvaluated: false,
        MatchingAdapters: [],
        WorkerInstalled: true,
        BrokerInstalled: true,
        BrokerAcquireAndMaterializeAvailable: true,
        Workspace: null);

    public Exception? Throw { get; set; }
    public Func<CancellationToken, Task<EnvironmentAssessmentResult>>? RunOverride { get; set; }

    public Task<EnvironmentAssessmentResult> RunAsync(EnvironmentAssessmentRequest request, WorkflowContext context, CancellationToken cancellationToken)
        => RunOverride is not null ? RunOverride(cancellationToken) : Throw is null ? Task.FromResult(Result) : Task.FromException<EnvironmentAssessmentResult>(Throw);
}

public sealed class FakeMaterializationWorkflow : IMaterializationWorkflow
{
    public Exception? Throw { get; set; }

    public Action<WorkflowContext>? OnRun { get; set; }

    public MaterializationWorkflowResult Result { get; set; } = new(
        TestDoubles.Verified(),
        "C:\\out\\workspace.json",
        new AccountIdentity(AccountIdentityState.Candidate, null, UserConfirmationState.Confirmed),
        "profile-id",
        "materialization-id");

    public async Task<MaterializationWorkflowResult> RunAsync(MaterializationWorkflowRequest request, WorkflowContext context, CancellationToken cancellationToken)
    {
        OnRun?.Invoke(context);
        if (Throw is not null)
        {
            throw Throw;
        }

        // Mirror the real workflow: block on the host's confirmation port.
        var report = new AccountIdentityReport("wxid_owner", AccountIdentityState.Candidate, null);
        var confirmation = await context.AccountConfirmation.ConfirmAsync(report, cancellationToken).ConfigureAwait(false);
        if (!confirmation.Confirmed)
        {
            throw new AppFailureException(ErrorCode.AccountConfirmationRequired, "Account confirmation was declined.");
        }

        return Result;
    }
}

public sealed class FakeWorkspaceWorkflow : IWorkspaceWorkflow
{
    public MaterializationRecoveryAssessment Assessment { get; set; } = new(
        "C:\\out",
        MaterializationCommitStates.FailedRecoverable,
        CanRecover: true,
        WorkspaceDocumentPresent: false);

    public VerifiedLocalWorkspace RecoveryResult { get; set; } = TestDoubles.Verified();

    public MaterializationRecoveryRequest? LastRecoveryRequest { get; private set; }

    public Task<WorkspaceCreateResult> CreateAsync(WorkspaceCreateRequest request, WorkflowContext context, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<VerifiedLocalWorkspace> VerifyAsync(string workspacePath, WorkflowContext context, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<VerifiedLocalWorkspace> RecoverMaterializationAsync(MaterializationRecoveryRequest request, WorkflowContext context, CancellationToken cancellationToken)
    {
        LastRecoveryRequest = request;
        return Task.FromResult(RecoveryResult);
    }

    public Task<VerifiedLocalWorkspace> RepairMaterializationAsync(MaterializationRecoveryRequest request, WorkflowContext context, CancellationToken cancellationToken)
        => Task.FromResult(RecoveryResult);

    public Task<MaterializationRecoveryAssessment> AssessMaterializationRecoveryAsync(string outputDirectory, string? workspaceOutputPath, WorkflowContext context, CancellationToken cancellationToken)
        => Task.FromResult(Assessment with { OutputDirectory = Path.GetFullPath(outputDirectory) });

    public Task<WorkspaceDeletionPreview> PreviewDeleteMaterializedAsync(string workspacePath, WorkflowContext context, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<WorkspaceDeletionResult> DeleteMaterializedAsync(string workspacePath, WorkflowContext context, CancellationToken cancellationToken)
        => throw new NotSupportedException();
}

public sealed class FakeSnapshotWorkflow : ISnapshotWorkflow
{
    public SnapshotWorkflowRequest? LastRequest { get; private set; }

    public Task<SnapshotWorkflowResult> RunAsync(
        SnapshotWorkflowRequest request,
        WorkflowContext context,
        CancellationToken cancellationToken)
    {
        LastRequest = request;
        var manifest = new SnapshotManifest(
            request.SourceDirectory,
            request.OutputDirectory,
            DateTimeOffset.UtcNow,
            Files: []);
        return Task.FromResult(new SnapshotWorkflowResult(
            manifest,
            new SnapshotSourceIdentity("wxid_owner_0000000000000000", "wxid_owner", null, 1),
            Path.Combine(request.OutputDirectory, ".wechatvoice", "snapshot-manifest.json")));
    }
}

public sealed class FakeDataSourceDiscovery : IWeixinDataSourceDiscovery
{
    public WeixinDataSourceDiscoveryResult Result { get; set; } = new([], false, 0);

    public int CallCount { get; private set; }

    public Func<CancellationToken, Task<WeixinDataSourceDiscoveryResult>>? DiscoverOverride { get; set; }

    public IReadOnlyList<WeixinDataSourceCandidate> Discover(
        IEnumerable<string>? roots = null,
        WeixinDataSourceDiscoveryOptions? options = null)
        => Result.Candidates;

    public async Task<IReadOnlyList<WeixinDataSourceCandidate>> DiscoverAsync(
        IEnumerable<string>? roots = null,
        WeixinDataSourceDiscoveryOptions? options = null,
        CancellationToken cancellationToken = default)
        => (await DiscoverDetailedAsync(roots, options, cancellationToken).ConfigureAwait(false)).Candidates;

    public Task<WeixinDataSourceDiscoveryResult> DiscoverDetailedAsync(
        IEnumerable<string>? roots = null,
        WeixinDataSourceDiscoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        cancellationToken.ThrowIfCancellationRequested();
        return DiscoverOverride is null ? Task.FromResult(Result) : DiscoverOverride(cancellationToken);
    }
}

public sealed class FakeWeixinProcessProbe : IWeixinProcessProbe
{
    public IReadOnlyList<WeChatVoice.Windows.WeChatProcessInfo> Running { get; set; } = [];

    public IReadOnlyList<WeChatVoice.Windows.WeChatProcessInfo> ListRunning() => Running;
}

public sealed class FakeFolderPicker : IDesktopFolderPicker
{
    public string? NextPath { get; set; }

    public void Attach(Avalonia.Controls.TopLevel owner)
    {
    }

    public Task<string?> PickFolderAsync(string title, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(NextPath);
    }

    public Task<string?> PickFileAsync(string title, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(NextPath);
    }
}

public sealed class FakeContactWorkflow : IContactDiscoveryWorkflow
{
    public ContactDiscoveryRequest? LastRequest { get; private set; }

    public Task<ContactDiscoveryResult> RunAsync(ContactDiscoveryRequest request, WorkflowContext context, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(new ContactDiscoveryResult(
            [
                new ContactRecord("contact-a", "wxid_a", "A", "Remark A", Nickname: "Nick A"),
                new ContactRecord("contact-b", "wxid_b", "B", "Remark B", Nickname: "Nick B"),
            ],
            TestDoubles.Verified(),
            Path.GetFullPath(request.WorkspacePath)));
    }
}

public sealed class FakeScanWorkflow : IVoiceScanWorkflow
{
    public VoiceScanWorkflowResult Result { get; set; } = new(
        new VoiceScanReport(
            MatchedVoiceCount: 4,
            TotalDurationMs: 400,
            EarliestOccurredAtUtc: null,
            LatestOccurredAtUtc: null,
            ShardCounts: new Dictionary<string, int>(),
            UnassociatedMediaCount: 1,
            EmptyBlobCount: 1,
            SuspectedDuplicateCount: 0,
            InvalidHeaderCount: 1,
            AmbiguousPayloadCount: 1,
            ExportableVoiceCount: 1,
            TotalPayloadBytes: 10,
            ResultSetFingerprint: "fake-result-set"),
        TestDoubles.Verified());

    public VoiceScanWorkflowRequest? LastRequest { get; private set; }

    public Func<CancellationToken, Task<VoiceScanWorkflowResult>>? RunOverride { get; set; }

    public Task<VoiceScanWorkflowResult> RunAsync(VoiceScanWorkflowRequest request, WorkflowContext context, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return CompleteResultAsync(request, cancellationToken);
    }

    private async Task<VoiceScanWorkflowResult> CompleteResultAsync(VoiceScanWorkflowRequest request, CancellationToken cancellationToken)
    {
        var result = RunOverride is not null ? await RunOverride(cancellationToken).ConfigureAwait(false) : Result;
        if (result.Selection is not null)
        {
            return result;
        }

        var direction = request.Direction ?? VoiceDirection.Incoming;
        var report = result.Report;
        var selection = new PreparedVoiceSelection(
            Path.GetFullPath(request.WorkspacePath),
            result.Workspace.Workspace.WorkspaceId,
            result.Workspace.DataSet.DataSetId,
            result.Workspace.DataSet.AccountId ?? "wxid_owner",
            result.Workspace.DataSet.SnapshotId,
            result.Workspace.DataSet.AdapterId ?? "fake-adapter",
            "fake-v1",
            request.ExpectedContactId ?? "contact-b",
            request.ContactUsername ?? "wxid_b",
            direction,
            request.From?.ToUniversalTime(),
            request.To?.ToUniversalTime(),
            request.MaximumResults,
            request.DeepScan,
            request.ResolveDurations,
            request.MinimumDurationMs,
            request.MaximumDurationMs,
            request.MinimumPayloadBytes,
            request.MaximumPayloadBytes,
            "fake-query",
            report.ResultSetFingerprint,
            report.ExportableVoiceCount,
            report.TotalPayloadBytes,
            PreparedVoiceSelection.CurrentSelectionEngineVersion,
            PreparedVoiceSelection.NoDurationResolverVersion,
            report,
            Records:
            [
                new VoiceRecord(
                    "fake-message",
                    "contact-b",
                    DateTimeOffset.UnixEpoch,
                    direction,
                    new VoicePayloadLocator("media", 0, "fake-message"),
                    SourceDatabase: "messages.db",
                    ShardNumber: 0,
                    SnapshotId: "snapshot-fake",
                    AdapterId: "fake-adapter",
                    AccountId: "wxid_owner",
                    PayloadByteLength: 10,
                    DurationMs: 100,
                    SpeakerId: direction == VoiceDirection.Incoming ? "wxid_b" : "wxid_owner",
                    DataSetId: "dataset-fake",
                    AdapterVersion: "fake-v1",
                    AdapterFamily: "fake-adapter",
                    AccountStableId: "wxid_owner",
                    ConversationStableId: "contact-b",
                    MessagePrimaryKey: "fake-message",
                    MediaPrimaryKey: "media:fake-message",
                    PayloadState: VoicePayloadState.Linked),
            ]);
        return result with { Selection = selection };
    }
}

public sealed class FakeExportWorkflow : IVoiceExportWorkflow
{
    public VoiceExportWorkflowRequest? LastRequest { get; private set; }
    public PreparedVoiceSelection? LastPlan { get; private set; }
    public ExportDestination? LastDestination { get; private set; }

    public VoiceExportManifest Manifest { get; set; } = new(
        DateTimeOffset.UtcNow,
        Entries: [new VoiceExportEntry("m1", "wxid_peer", DateTimeOffset.UtcNow, VoiceDirection.Incoming, "original/x.silk", 10, "aa", null, SourceStableKey: "k", WasSkipped: false)],
        Failures: [new VoiceExportFailure("m2", "payload-invalid-header", "invalid header")],
        RunId: "run-1",
        RunStatus: ExportRunStatus.CompletedWithFailures);

    public Task<VoiceExportWorkflowResult> RunAsync(PreparedVoiceSelection plan, ExportDestination destination, WorkflowContext context, CancellationToken cancellationToken)
    {
        LastPlan = plan;
        LastDestination = destination;
        LastRequest = new VoiceExportWorkflowRequest(
            plan.WorkspaceDocumentPath,
            destination.OutputDirectory,
            plan.ContactUsername,
            Direction: plan.Direction,
            From: plan.FromUtc,
            To: plan.ToUtc,
            MaximumResults: plan.MaximumResults,
            ExpectedResultSetFingerprint: plan.ResultSetFingerprint,
            ExpectedResultCount: plan.ResultCount,
            ExpectedTotalPayloadBytes: plan.TotalPayloadBytes,
            ExpectedContactId: plan.ContactId,
            MinimumDurationMs: plan.MinimumDurationMs,
            MaximumDurationMs: plan.MaximumDurationMs,
            MinimumPayloadBytes: plan.MinimumPayloadBytes,
            MaximumPayloadBytes: plan.MaximumPayloadBytes,
            ResolveDurations: plan.ResolveDurations);
        return Task.FromResult(new VoiceExportWorkflowResult(Manifest, TestDoubles.Verified()));
    }

    public Task<ExportVerificationResult> VerifyAsync(ExportVerificationRequest request, WorkflowContext context, CancellationToken cancellationToken)
        => Task.FromResult(new ExportVerificationResult(
            Path.GetFullPath(request.ExportDirectory),
            request.RunId ?? Manifest.RunId,
            true,
            new string('a', 64),
            Manifest.Entries.Count,
            0,
            0,
            true,
            true,
            true,
            []));

    public Task<ExportRepairResult> RepairAsync(ExportRepairRequest request, WorkflowContext context, CancellationToken cancellationToken)
        => Task.FromResult(new ExportRepairResult(
            new ExportVerificationResult(
                Path.GetFullPath(request.ExportDirectory),
                request.RunId ?? Manifest.RunId,
                true,
                new string('a', 64),
                Manifest.Entries.Count,
                0,
                0,
                true,
                true,
                true,
                []),
            Path.Combine(Path.GetFullPath(request.ExportDirectory), "runs", Manifest.RunId + ".jsonl")));

    public Task<VoiceExportManifest> RecoverRunAsync(string journalPath, CancellationToken cancellationToken)
        => Task.FromResult(Manifest);
}

public static class TestDoubles
{
    public static LocalWorkspace Workspace()
        => new LocalWorkspace(
            "workspace-fake",
            "C:\\fake\\root",
            new WeChatDataSet("dataset-fake", "wxid_owner", [], "snapshot-fake", "fake-adapter"),
            DateTimeOffset.UtcNow,
            Issues: [],
            AdapterCandidates: []);

    public static VerifiedLocalWorkspace Verified() => new(Workspace(), DateTimeOffset.UtcNow);

    public sealed class SilentConfirmation : IAccountConfirmation
    {
        public Task<AccountConfirmation> ConfirmAsync(AccountIdentityReport report, CancellationToken cancellationToken)
            => Task.FromResult(new AccountConfirmation(true, report.AccountCandidate));
    }

    /// <summary>
    /// Deterministic decoder factory that emits a fixed 16-bit PCM WAV so that
    /// WAV training builds and audio previews can be exercised headlessly
    /// without a real SILK decoder.
    /// </summary>
    public sealed class FakeDecoderFactory : IVoiceDecoderFactory
    {
        public const string DecoderIdentity = "fake-decoder-v1";

        public IVoiceDecoder? Create(int sampleRate)
            => new FixedWavDecoder(sampleRate);
    }

    private sealed class FixedWavDecoder(int requestedSampleRate) : IVoiceDecoder, IVoiceDecoderIdentity
    {
        public string DecoderIdentity => FakeDecoderFactory.DecoderIdentity;

        public async Task DecodeAsync(Stream input, Stream output, CancellationToken cancellationToken)
        {
            var dataBytes = 480;
            var wav = new byte[44 + dataBytes];
            "RIFF"u8.CopyTo(wav);
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), (uint)(wav.Length - 8));
            "WAVE"u8.CopyTo(wav.AsSpan(8));
            "fmt "u8.CopyTo(wav.AsSpan(12));
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16), 16);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24), (uint)requestedSampleRate);
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28), (uint)(requestedSampleRate * 2));
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), 16);
            "data"u8.CopyTo(wav.AsSpan(36));
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), (uint)dataBytes);
            await output.WriteAsync(wav, cancellationToken).ConfigureAwait(false);
        }
    }
}
