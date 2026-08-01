using WeChatVoice.Application;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Adapters;
using WeChatVoice.Workflows.Broker;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Workflows.Composition;

/// <summary>
/// The single composition root for the shared workflows, used by the CLI and
/// the Desktop host. Hosts never compose Infrastructure themselves; they build
/// this root (optionally overriding the broker trust policy and the broker
/// install directory) and call the workflow interfaces.
/// </summary>
public sealed class WorkflowCompositionRoot
{
    public WorkflowCompositionRoot(
        IAccountConfirmation accountConfirmation,
        bool allowDevelopmentBroker = false,
        string? brokerDirectory = null,
        IEnvironmentAssessmentWorkflow? environmentAssessment = null,
        ISnapshotWorkflow? snapshot = null,
        IMaterializationWorkflow? materialization = null,
        IWorkspaceWorkflow? workspace = null,
        IContactDiscoveryWorkflow? contactDiscovery = null,
        IVoiceScanWorkflow? voiceScan = null,
        IVoiceExportWorkflow? voiceExport = null)
    {
        ArgumentNullException.ThrowIfNull(accountConfirmation);
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

        EnvironmentAssessment = environmentAssessment ?? new EnvironmentAssessmentWorkflow(loader);
        Snapshot = snapshot ?? new SnapshotWorkflow();
        Materialization = materialization ?? new MaterializationWorkflow(brokerExecutor);
        Workspace = workspace ?? new WorkspaceWorkflow(loader: loader);
        ContactDiscovery = contactDiscovery ?? new ContactDiscoveryWorkflow(opener);
        VoiceScan = voiceScan ?? new VoiceScanWorkflow(opener, contactResolver);
        VoiceExport = voiceExport ?? new VoiceExportWorkflow(opener, contactResolver);
        AccountConfirmation = accountConfirmation;
        AllowDevelopmentBroker = allowDevelopmentBroker;
    }

    public IEnvironmentAssessmentWorkflow EnvironmentAssessment { get; }

    public ISnapshotWorkflow Snapshot { get; }

    public IMaterializationWorkflow Materialization { get; }

    public IWorkspaceWorkflow Workspace { get; }

    public IContactDiscoveryWorkflow ContactDiscovery { get; }

    public IVoiceScanWorkflow VoiceScan { get; }

    public IVoiceExportWorkflow VoiceExport { get; }

    public IAccountConfirmation AccountConfirmation { get; }

    /// <summary>True when the host opted into the unsigned development Broker.</summary>
    public bool AllowDevelopmentBroker { get; }
}
