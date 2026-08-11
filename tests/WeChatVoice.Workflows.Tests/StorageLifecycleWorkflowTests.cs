using System.Text.Json;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Storage;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Workflows.Tests;

/// <summary>
/// Covers the shared storage lifecycle workflow: how app-owned storage is
/// classified and reclaimed. Inventory and preview are read-only; cleanup only
/// removes independent transient objects and expired-recoverable workspaces
/// through the deletion boundary, never user assets or reusable workspaces.
/// </summary>
public sealed class StorageLifecycleWorkflowTests
{
    private static readonly JsonSerializerOptions CamelCase = new(JsonSerializerDefaults.Web);
    [Fact]
    public async Task Inventory_classifies_and_totals_app_owned_objects()
    {
        using var temp = new StorageTemp();
        var roots = temp.Roots;
        var completed = temp.CreateSnapshot("acct-1", "op-1", withManifest: true);
        var staging = temp.CreateSnapshot("acct-1", "op-2", withManifest: false);
        var transient = temp.CreateTransient("staging-1");

        var summary = await new ManagedStorageInventory(roots).InventoryAsync(CancellationToken.None);

        Assert.True(summary.SnapshotBytes >= 1);
        Assert.Contains(summary.Assets, item => item.Path == completed && item.Kind == StorageAssetKind.ReusableIntermediate);
        Assert.Contains(summary.Assets, item => item.Path == staging && item.Kind == StorageAssetKind.Transient);
        Assert.Contains(summary.Assets, item => item.Path == transient && item.Kind == StorageAssetKind.Transient);
        Assert.True(summary.TempBytes >= 1);
        Assert.True(summary.SafelyReclaimableBytes >= 1);
        // Export and Dataset are user-owned paths and are never reported as app-owned.
        Assert.Equal(0, summary.ExportBytes);
        Assert.Equal(0, summary.DatasetBytes);
    }

    [Fact]
    public async Task Inventory_classifies_a_recoverable_workspace()
    {
        using var temp = new StorageTemp();
        var workspace = temp.CreateRecoverableWorkspace("mat-1");

        var summary = await new ManagedStorageInventory(temp.Roots).InventoryAsync(CancellationToken.None);

        var asset = Assert.Single(summary.Assets, item => item.Path == workspace);
        Assert.Equal(StorageAssetKind.RecoverableIntermediate, asset.Kind);
        Assert.True(summary.RecoverableBytes >= 1);
        Assert.True(summary.SafelyReclaimableBytes >= 1);
    }

    [Fact]
    public async Task Preview_returns_items_without_deleting_anything()
    {
        using var temp = new StorageTemp();
        var transient = temp.CreateTransient("staging-1");
        var workflow = new StorageLifecycleWorkflow(inventory: new ManagedStorageInventory(temp.Roots));

        var preview = await workflow.PreviewCleanupAsync(
            new StorageCleanupRequest(),
            Context(),
            CancellationToken.None);

        Assert.Contains(preview.Items, item => item.Path == transient);
        Assert.True(Directory.Exists(transient));
    }

    [Fact]
    public async Task Cleanup_removes_transient_objects()
    {
        using var temp = new StorageTemp();
        var transient = temp.CreateTransient("staging-1");
        var workflow = new StorageLifecycleWorkflow(inventory: new ManagedStorageInventory(temp.Roots));

        var result = await workflow.CleanupAsync(
            new StorageCleanupRequest(),
            Context(),
            CancellationToken.None);

        Assert.True(result.DeletedCount >= 1);
        Assert.False(Directory.Exists(transient));
    }

    [Fact]
    public async Task Cleanup_skips_recoverable_workspace_within_retention()
    {
        using var temp = new StorageTemp();
        var workspace = temp.CreateRecoverableWorkspace("mat-recent");
        var fakeDelete = new FakeDeleteWorkspace();
        var workflow = new StorageLifecycleWorkflow(
            inventory: new ManagedStorageInventory(temp.Roots),
            deleteWorkspace: fakeDelete);

        var result = await workflow.CleanupAsync(
            new StorageCleanupRequest(),
            Context(),
            CancellationToken.None);

        Assert.True(Directory.Exists(workspace));
        Assert.Equal(0, fakeDelete.RunCalls);
    }

    [Fact]
    public async Task Cleanup_skips_locked_recoverable_workspace()
    {
        using var temp = new StorageTemp();
        var workspace = temp.CreateRecoverableWorkspace("mat-locked", locked: true);
        var fakeDelete = new FakeDeleteWorkspace();
        var workflow = new StorageLifecycleWorkflow(
            inventory: new ManagedStorageInventory(temp.Roots),
            deleteWorkspace: fakeDelete);

        var result = await workflow.CleanupAsync(
            new StorageCleanupRequest(ForceRecoverable: true),
            Context(),
            CancellationToken.None);

        Assert.True(Directory.Exists(workspace));
        Assert.Equal(0, fakeDelete.RunCalls);
        Assert.Contains(result.SkippedReasons, reason => reason.Contains("锁", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cleanup_routes_expired_recoverable_workspace_to_the_delete_boundary()
    {
        using var temp = new StorageTemp();
        var workspace = temp.CreateRecoverableWorkspace("mat-old", age: TimeSpan.FromDays(30));
        temp.CreateWorkspaceDocument("mat-old");
        var fakeDelete = new FakeDeleteWorkspace(canned: new WorkspaceDeletionResult(
            "mat-old", workspace, 1, 1, WorkspaceDocumentPath: temp.Combine("mat-old.workspace.json")));
        var workflow = new StorageLifecycleWorkflow(
            inventory: new ManagedStorageInventory(temp.Roots),
            deleteWorkspace: fakeDelete);

        var result = await workflow.CleanupAsync(
            new StorageCleanupRequest(),
            Context(),
            CancellationToken.None);

        Assert.Equal(1, fakeDelete.RunCalls);
        Assert.Equal(1, result.DeletedCount);
    }

    [Fact]
    public async Task Cleanup_skips_reparse_point_objects()
    {
        using var temp = new StorageTemp();
        var linkPath = temp.Combine("prepared-selection", "reparse-point");
        var target = temp.Combine("prepared-selection", "target");
        Directory.CreateDirectory(target);
        var workflow = new StorageLifecycleWorkflow(inventory: new ManagedStorageInventory(temp.Roots));

        try
        {
            Directory.CreateSymbolicLink(linkPath, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // Symbolic links are not permitted in this environment; skip.
        }

        var result = await workflow.CleanupAsync(
            new StorageCleanupRequest(),
            Context(),
            CancellationToken.None);

        Assert.True(Directory.Exists(linkPath));
        Assert.Contains(result.SkippedReasons, reason => reason.Contains("Reparse", StringComparison.Ordinal));
    }

    private static WorkflowContext Context() => new(new TestConfirmation());

    private sealed class TestConfirmation : IAccountConfirmation
    {
        public Task<AccountConfirmation> ConfirmAsync(AccountIdentityReport report, CancellationToken cancellationToken)
            => Task.FromResult(new AccountConfirmation(true, report.AccountCandidate));
    }

    private sealed class FakeDeleteWorkspace(WorkspaceDeletionResult? canned = null) : IDeleteMaterializedWorkspaceWorkflow
    {
        private readonly WorkspaceDeletionResult _canned = canned ?? new WorkspaceDeletionResult("w", "root", 0, 0);
        public int RunCalls { get; private set; }

        public Task<WorkspaceDeletionPreview> PreviewAsync(string workspacePath, WorkflowContext context, CancellationToken cancellationToken)
            => Task.FromResult(new WorkspaceDeletionPreview("w", "root", 0, 0));

        public Task<WorkspaceDeletionResult> RunAsync(string workspacePath, WorkflowContext context, CancellationToken cancellationToken)
        {
            RunCalls++;
            return Task.FromResult(_canned);
        }
    }

    private sealed class StorageTemp : IDisposable
    {
        public StorageTemp()
        {
            Root = Path.Combine(Path.GetTempPath(), "WeChatVoiceToolkit.StorageLifecycleTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Roots = new StorageRoots(Combine(), Combine("Temp"));
        }

        public string Root { get; }
        public StorageRoots Roots { get; }

        public string Combine(params string[] segments) => Path.Combine([Root, .. segments]);

        public string CreateSnapshot(string account, string operation, bool withManifest)
        {
            var path = Combine("Data", "Snapshots", account, operation);
            Directory.CreateDirectory(path);
            File.WriteAllBytes(Path.Combine(path, "payload.bin"), new byte[1]);
            if (withManifest)
            {
                var metadata = Path.Combine(path, ".wechatvoice");
                Directory.CreateDirectory(metadata);
                File.WriteAllText(Path.Combine(metadata, "snapshot-manifest.json"), "{}");
            }

            return path;
        }

        public string CreateTransient(string name)
        {
            var path = Combine("Temp", "SnapshotsStaging", name);
            Directory.CreateDirectory(path);
            File.WriteAllBytes(Path.Combine(path, "temp.bin"), new byte[1]);
            return path;
        }

        public string CreateRecoverableWorkspace(string name, bool locked = false, TimeSpan? age = null)
        {
            var path = Combine("Data", "Workspaces", name);
            Directory.CreateDirectory(Path.Combine(path, ".wechatvoice"));
            var state = new MaterializationStateDocument(
                MaterializationCommitStates.FailedRecoverable,
                DateTimeOffset.UtcNow - (age ?? TimeSpan.Zero),
                Binding: new MaterializationStateBinding("snap-1", "backend-1"));
            File.WriteAllText(
                Path.Combine(path, ".wechatvoice", "materialization-state.json"),
                JsonSerializer.Serialize(state, CamelCase));
            if (locked)
            {
                File.WriteAllText(Path.Combine(path, ".wechatvoice", "materialization.lock"), string.Empty);
            }

            File.WriteAllBytes(Path.Combine(path, "db.bin"), new byte[1]);
            Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow - (age ?? TimeSpan.Zero));
            return path;
        }

        public string CreateWorkspaceDocument(string name)
        {
            var path = Combine("Data", "Workspaces", name + ".workspace.json");
            File.WriteAllText(path, "{}");
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}