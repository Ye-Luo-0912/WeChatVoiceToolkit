using WeChatVoice.Application;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Adapters;
using WeChatVoice.Infrastructure.Audio;
using WeChatVoice.Infrastructure.Export;
using WeChatVoice.Infrastructure.Storage;
using WeChatVoice.Workflows.Broker;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Workflows.Composition;

/// <summary>
/// The single composition root for the shared workflows, used by the CLI and
/// the Desktop host. Hosts never compose Infrastructure themselves; they build
/// this root (optionally overriding the broker trust policy and the broker
/// install directory) and call the workflow interfaces.
/// </summary>
public sealed class WorkflowCompositionRoot : IAsyncDisposable
{
    private readonly TemporaryFileCleanupQueue _cleanupQueue;
    private readonly IAsyncDisposable? _durationResolver;
    private readonly DecoderConfigurationStore _decoderConfiguration;
    private int _disposed;

    public WorkflowCompositionRoot(
        IAccountConfirmation accountConfirmation,
        bool allowDevelopmentBroker = false,
        string? brokerDirectory = null,
        IEnvironmentAssessmentWorkflow? environmentAssessment = null,
        IProjectStateWorkflow? projectState = null,
        ISnapshotWorkflow? snapshot = null,
        IMaterializationWorkflow? materialization = null,
        IWorkspaceWorkflow? workspace = null,
        IContactDiscoveryWorkflow? contactDiscovery = null,
        IVoiceScanWorkflow? voiceScan = null,
        IVoiceExportWorkflow? voiceExport = null,
        IDatasetCurationWorkflow? datasetCuration = null,
        IVoiceDurationResolver? voiceDurationResolver = null,
        IStorageLifecycleWorkflow? storageLifecycle = null,
        string? appDataDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(accountConfirmation);
        _cleanupQueue = new TemporaryFileCleanupQueue();
        PreparedSelectionSpool.CleanupOrphans(cleanupQueue: _cleanupQueue);
        var storageRoots = StorageRootsFor(appDataDirectory);
        new StartupOrphanSweeper(storageRoots).Sweep(cleanupQueue: _cleanupQueue);
        _decoderConfiguration = new DecoderConfigurationStore(storageRoots.AppDataRoot);
        var loader = new Workspaces.WorkspaceLoader();
        var adapters = BuiltInAdapters.Create();
        var resolver = new DataSetAdapterResolver(adapters);
        var opener = new Workspaces.VoiceCatalogOpener(loader, resolver);
        var contactResolver = new Workspaces.ContactResolver();

        var trustPolicy = allowDevelopmentBroker
            ? (IBrokerTrustPolicy)new DevelopmentBrokerTrustPolicy()
            : new ReleaseBrokerTrustPolicy();
        IBrokerClient brokerClient = new KeyBrokerClient(trustPolicy, brokerDirectory);
        var brokerExecutor = new BrokerMaterializationExecutor(brokerClient);

        EnvironmentAssessment = environmentAssessment ?? new EnvironmentAssessmentWorkflow(loader, brokerTrustPolicy: trustPolicy);
        Snapshot = snapshot ?? new SnapshotWorkflow();
        Materialization = materialization ?? new MaterializationWorkflow(brokerExecutor);
        Workspace = workspace ?? new WorkspaceWorkflow(loader: loader);
        ProjectState = projectState ?? new ProjectStateWorkflow(loader: loader, workspace: Workspace);
        ContactDiscovery = contactDiscovery ?? new ContactDiscoveryWorkflow(opener);
        var configuredDecoder = voiceDurationResolver ?? CreateDurationResolver(_cleanupQueue);
        _durationResolver = configuredDecoder as IAsyncDisposable;
        DurationAnalysisAvailable = configuredDecoder is not null;
        DecoderStatusReport = CreateDecoderStatusReport();
        Func<VerifiedLocalWorkspace, IVoiceDurationCache>? durationCacheFactory = configuredDecoder is null
            ? null
            : workspaceResult => new JsonlVoiceDurationCache(
                VoiceDurationCachePath.ForWorkspace(workspaceResult),
                configuredDecoder is IVersionedVoiceDurationResolver versioned
                    ? versioned.DecoderVersion
                    : DecoderVoiceDurationResolver.CurrentDecoderVersion);
        Func<VerifiedLocalWorkspace, IVoicePayloadHashCache> deepScanCacheFactory = workspaceResult =>
            new JsonlVoicePayloadHashCache(VoicePayloadHashCachePath.ForWorkspace(workspaceResult));
        VoiceScan = voiceScan ?? new VoiceScanWorkflow(opener, contactResolver, configuredDecoder, durationCacheFactory, deepScanCacheFactory, _cleanupQueue);
        VoiceExport = voiceExport ?? new VoiceExportWorkflow(opener, contactResolver, durationCacheFactory, configuredDecoder, cleanupQueue: _cleanupQueue);
        DatasetCuration = datasetCuration
            ?? new DatasetCurationWorkflow(
                datasetBuildService: new DatasetBuildService(
                    new SilkVoiceDecoderFactory(_decoderConfiguration)));
        StorageLifecycle = storageLifecycle
            ?? new StorageLifecycleWorkflow(
                inventory: new ManagedStorageInventory(storageRoots));
        AccountConfirmation = accountConfirmation;
        AllowDevelopmentBroker = allowDevelopmentBroker;
    }

    public IEnvironmentAssessmentWorkflow EnvironmentAssessment { get; }

    public IProjectStateWorkflow ProjectState { get; }

    public ISnapshotWorkflow Snapshot { get; }

    public IMaterializationWorkflow Materialization { get; }

    public IWorkspaceWorkflow Workspace { get; }

    public IContactDiscoveryWorkflow ContactDiscovery { get; }

    public IVoiceScanWorkflow VoiceScan { get; }

    public IVoiceExportWorkflow VoiceExport { get; }

    public IDatasetCurationWorkflow DatasetCuration { get; }

    public IStorageLifecycleWorkflow StorageLifecycle { get; }

    public IAccountConfirmation AccountConfirmation { get; }

    /// <summary>True when the host opted into the unsigned development Broker.</summary>
    public bool AllowDevelopmentBroker { get; }

    public bool DurationAnalysisAvailable { get; }

    /// <summary>
    /// Non-sensitive status of the configured duration decoder. The UI uses it
    /// to explain why duration analysis is or is not available.
    /// </summary>
    public Core.Models.DecoderStatusReport DecoderStatusReport { get; private set; }

    /// <summary>
    /// Persists a user-facing reviewed decoder worker path and updates the
    /// status report. Passing null clears the configuration. The environment
    /// variables remain the advanced/development path.
    /// </summary>
    public Core.Models.DecoderStatusReport ConfigureDecoder(string? workerPath)
    {
        _decoderConfiguration.SetWorkerPath(workerPath);
        DecoderStatusReport = new DecoderStatusInspector(_decoderConfiguration).Report();
        return DecoderStatusReport;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_durationResolver is not null)
            {
                await _durationResolver.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await _cleanupQueue.RetryPendingAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static StorageRoots StorageRootsFor(string? appDataDirectory)
    {
        var appData = string.IsNullOrWhiteSpace(appDataDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WeChatVoiceToolkit")
            : Path.GetFullPath(appDataDirectory);
        return new StorageRoots(appData, Path.Combine(Path.GetTempPath(), "WeChatVoiceToolkit"));
    }

    private Core.Models.DecoderStatusReport CreateDecoderStatusReport()
        => new DecoderStatusInspector(_decoderConfiguration).Report();

    private IVoiceDurationResolver? CreateDurationResolver(ITemporaryFileCleanupQueue cleanupQueue)
    {
        var inspector = new DecoderStatusInspector(_decoderConfiguration);
        var workerPath = inspector.DiscoverWorkerPath();
        if (!string.IsNullOrWhiteSpace(workerPath) && File.Exists(workerPath))
        {
            return new DecoderVoiceDurationResolver(new ExternalSilkDecoderWorker(workerPath, cleanupQueue: cleanupQueue), cleanupQueue);
        }

        var path = Environment.GetEnvironmentVariable("WECHATVOICE_SILK_DECODER_PATH");
        return string.IsNullOrWhiteSpace(path) || !File.Exists(path)
            ? null
            : new DecoderVoiceDurationResolver(new ExternalSilkDecoder(path, cleanupQueue: cleanupQueue), cleanupQueue);
    }
}
