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
        Snapshot = null;
        SnapshotDirectory = null;
        Materialization = null;
        Workspace = null;
        WorkspacePath = null;
        SelectedContact = null;
        Scan = null;
        SelectionPlan = null;
        LastExportRun = null;
    }
}
