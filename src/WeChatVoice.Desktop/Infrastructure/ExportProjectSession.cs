using CommunityToolkit.Mvvm.ComponentModel;
using WeChatVoice.Core.Models;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Desktop.Infrastructure;

/// <summary>
/// Application-scoped state for one guided export project. It carries only
/// verified workflow results and user-facing selections; it never replaces a
/// workflow boundary or stores database contents.
/// </summary>
public sealed partial class ExportProjectSession : ObservableObject
{
    [ObservableProperty] private string? _sourceDirectory;
    [ObservableProperty] private EnvironmentAssessmentResult? _environmentAssessment;
    [ObservableProperty] private SnapshotWorkflowResult? _snapshot;
    [ObservableProperty] private string? _snapshotDirectory;
    [ObservableProperty] private MaterializationWorkflowResult? _materialization;
    [ObservableProperty] private VerifiedLocalWorkspace? _workspace;
    [ObservableProperty] private string? _workspacePath;
    [ObservableProperty] private ContactRecord? _selectedContact;
    [ObservableProperty] private VoiceScanWorkflowResult? _scan;
    [ObservableProperty] private VoiceSelectionPlan? _selectionPlan;
    [ObservableProperty] private VoiceExportWorkflowResult? _lastExportRun;
    [ObservableProperty] private string? _exportDirectory;

    public void ResetFromSource(string sourceDirectory)
    {
        SourceDirectory = sourceDirectory;
        // Environment trust is installation-scoped, not source-scoped. Keep
        // the completed Broker/Worker preflight when the user chooses a new
        // data source; clearing it would make the guided flow lose its own
        // prerequisite immediately before materialization.
        Snapshot = null;
        SnapshotDirectory = null;
        Materialization = null;
        Workspace = null;
        WorkspacePath = null;
        ClearVoiceSelection(clearContact: true);
        ExportDirectory = null;
    }

    /// <summary>
    /// Invalidates all downstream voice choices when a workspace or source is
    /// replaced. Keeping this in the application session gives every host
    /// page one authoritative invalidation rule.
    /// </summary>
    public void ClearVoiceSelection(bool clearContact)
    {
        if (clearContact)
        {
            SelectedContact = null;
        }

        Scan = null;
        SelectionPlan = null;
        LastExportRun = null;
    }
}
