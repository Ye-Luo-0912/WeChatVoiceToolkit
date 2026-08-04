using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Desktop.Infrastructure;
using WeChatVoice.Workflows.Broker;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Desktop.Smoke;

/// <summary>
/// Headless smoke suite for CI: no window is created, so it runs in a
/// non-interactive session. It exercises the composition root, the shared
/// Workflow State Machine (start/complete and cancel paths), the
/// recent-workspaces store, and the scrubbed log, then writes a marker file
/// and exits 0 (or non-zero with a message on failure).
/// </summary>
public static class SmokeCheckRunner
{
    public static int Run(bool releaseTrustSmoke = false)
    {
        DesktopServices? services = null;
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "WeChatVoiceToolkit.Smoke", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            // 1. Composition root constructs every workflow with a silent port.
            services = DesktopServices.Create(appDataDirectory: directory);
            Assert(services.Workflows.EnvironmentAssessment is not null, "EnvironmentAssessment workflow missing");
            Assert(services.Workflows.Snapshot is not null, "Snapshot workflow missing");
            Assert(services.Workflows.Materialization is not null, "Materialization workflow missing");
            Assert(services.Workflows.Workspace is not null, "Workspace workflow missing");
            Assert(services.Workflows.ContactDiscovery is not null, "ContactDiscovery workflow missing");
            Assert(services.Workflows.VoiceScan is not null, "VoiceScan workflow missing");
            Assert(services.Workflows.VoiceExport is not null, "VoiceExport workflow missing");

            if (releaseTrustSmoke)
            {
                var brokerPath = Path.Combine(AppContext.BaseDirectory, "WeChatVoice.KeyBroker.exe");
                var trust = new ReleaseBrokerTrustPolicy(installDirectory: AppContext.BaseDirectory).Verify(brokerPath);
                Assert(trust.Verified, $"release broker trust failed: {trust.NonSensitiveReason}");
                var brokerClient = new KeyBrokerClient(
                    new ReleaseBrokerTrustPolicy(installDirectory: AppContext.BaseDirectory),
                    AppContext.BaseDirectory);
                var selfTest = brokerClient.SelfTestAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert(string.Equals(selfTest.Status, "completed", StringComparison.Ordinal), "Broker self-test did not complete");
                Assert(string.Equals(selfTest.WorkerBundleStatus, "verified", StringComparison.Ordinal), "Broker self-test did not verify the Worker bundle");
                Assert(string.Equals(selfTest.WorkerSelfTestStatus, "completed", StringComparison.Ordinal), "Broker self-test did not start the SQLCipher Worker self-test");
            }

            // 2. State machine transitions: run -> complete.
            var machine = new WorkflowStateMachine();
            var transitions = new List<WorkflowStateTransition>();
            machine.Transitioned += (_, transition) => transitions.Add(transition);
            Assert(machine.TryStart(), "start failed");
            Assert(machine.State == WorkflowState.Running, "not running");
            Assert(!machine.TryStart(), "double start accepted");
            Assert(machine.TryEnterAwaitingUser(), "awaiting-user failed");
            Assert(machine.TryResumeFromUser(), "resume failed");
            Assert(machine.TryComplete(), "complete failed");
            Assert(machine.State == WorkflowState.Completed, "not completed");
            Assert(transitions.Count == 4, $"unexpected transition count {transitions.Count}");

            // 3. State machine: cancellation path.
            var cancelMachine = new WorkflowStateMachine();
            Assert(cancelMachine.TryStart(), "cancel start failed");
            Assert(cancelMachine.TryRequestCancellation(), "cancel request failed");
            Assert(cancelMachine.State == WorkflowState.Cancelling, "not cancelling");
            Assert(cancelMachine.TryCancel(), "cancel failed");
            Assert(cancelMachine.State == WorkflowState.Cancelled, "not cancelled");
            Assert(!cancelMachine.TryComplete(), "complete after cancel accepted");

            // 4. State machine: failure then retry.
            var retryMachine = new WorkflowStateMachine();
            Assert(retryMachine.TryStart(), "retry start failed");
            Assert(retryMachine.TryFail(), "fail failed");
            Assert(retryMachine.State == WorkflowState.Failed, "not failed");
            Assert(retryMachine.TryStart(), "retry after failure failed");
            Assert(retryMachine.TryComplete(), "retry complete failed");

            // 5. WorkflowRunHost runs a fake workflow through the real state
            //    machine with a direct marshaler (no UI thread in smoke).
            var host = new WorkflowRunHost(invokeOnUi: static action =>
            {
                action();
                return Task.CompletedTask;
            }, log: services.Log);
            var confirmation = new DialogAccountConfirmation();
            host.RunAsync(confirmation, (context, cancellationToken) =>
            {
                context.Report(OperationPhase.EnvironmentAssessment, OperationStageIds.DetectingWeixin);
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }).GetAwaiter().GetResult();
            Assert(host.StateMachine.State == WorkflowState.Completed, $"host run state {host.StateMachine.State}");
            Assert(host.StageId == OperationStageIds.DetectingWeixin, "stage not reported");

            // 6. Recent workspace store round-trip.
            var store = new RecentWorkspaceStore(directory);
            var dataSet = new WeChatDataSet("dataset-smoke", null, [], "snapshot-smoke", null);
            var workspace = new LocalWorkspace("workspace-smoke", directory, dataSet, DateTimeOffset.UtcNow, [], [], null);
            store.Add(new VerifiedLocalWorkspace(workspace, DateTimeOffset.UtcNow), Path.Combine(directory, "workspace.json"));
            var loaded = store.Load();
            Assert(loaded.Count == 1, $"store count {loaded.Count}");
            Assert(loaded[0].WorkspaceId == "workspace-smoke", "store round-trip mismatch");

            // 7. Scrubber: wxid_ and long hex never reach the log.
            var scrubbed = DesktopLog.Scrub("account wxid_sto5zbw1l3jk21 key ac599744a7ce7b65640ebe18c939c0d4e4a06cd039d89cddee7f1e9afc56875d");
            Assert(!scrubbed.Contains("sto5zbw1l3jk21", StringComparison.Ordinal), "wxid leaked");
            Assert(!scrubbed.Contains("ac599744", StringComparison.Ordinal), "hex leaked");

            // 8. WorkflowContext reports typed progress events.
            var captured = new List<OperationProgress>();
            var context = new WorkflowContext(new SilentAccountConfirmation(), new DirectProgress(captured));
            context.Report(OperationPhase.VoiceScan, OperationStageIds.QueryingVoices, "正在查询", 42);
            Assert(captured.Count == 1, "progress not reported");
            Assert(captured[0].Phase == OperationPhase.VoiceScan, "progress phase mismatch");
            Assert(captured[0].Stage.Id == OperationStageIds.QueryingVoices, "progress stage mismatch");
            Assert(captured[0].Stage.PercentComplete == 42, "progress percent mismatch");

            var marker = Path.Combine(directory, "smoke-ok.txt");
            File.WriteAllText(marker, "ok");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"smoke-check failed: {exception}");
            return 1;
        }
        finally
        {
            // The composition root may own a resident Decoder Worker. A
            // published smoke process must exercise the same shutdown path as
            // Desktop so the trust smoke cannot leave helper processes behind.
            services?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"smoke-check: {message}");
        }
    }

    private sealed class SilentAccountConfirmation : IAccountConfirmation
    {
        public Task<AccountConfirmation> ConfirmAsync(AccountIdentityReport report, CancellationToken cancellationToken)
            => Task.FromResult(new AccountConfirmation(false, null));
    }

    /// <summary>Synchronous progress sink so smoke assertions are deterministic.</summary>
    private sealed class DirectProgress(ICollection<OperationProgress> target) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => target.Add(value);
    }
}
