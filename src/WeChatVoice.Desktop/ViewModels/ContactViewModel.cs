using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeChatVoice.Core.Models;
using WeChatVoice.Desktop.Infrastructure;
using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Desktop.ViewModels;

/// <summary>
/// Contact discovery page. Only stable 1:1 contacts are shown (chatrooms are
/// excluded by the adapter); selection feeds scan and export by the exact
/// internal username.
/// </summary>
public sealed partial class ContactViewModel : PageViewModelBase
{
    public ContactViewModel(DesktopServices services)
        : this(services, invokeOnUi: null)
    {
    }

    internal ContactViewModel(DesktopServices services, Func<Action, Task>? invokeOnUi)
        : base(services, invokeOnUi)
    {
        WorkspacePath = services.Project.WorkspacePath;
    }

    public override string Title => "联系人";

    public override bool CanNavigate => Services.Project.Workspace is not null;

    public override string? NavigationHint => CanNavigate ? null : "请先完成物料化或加载 Workspace";

    [ObservableProperty]
    private string? _workspacePath;

    [ObservableProperty]
    private string? _searchTerm;

    [ObservableProperty]
    private IReadOnlyList<ContactRecord> _contacts = [];

    [ObservableProperty]
    private ContactRecord? _selectedContact;

    [ObservableProperty]
    private string? _accountSummary;

    [ObservableProperty]
    private string _contactSummary = "尚未加载";

    /// <summary>True when the ListBox has a concrete stable contact selection.</summary>
    public bool HasSelectedContact => SelectedContact is not null;

    public string SelectedContactSummary
        => SelectedContact is { } contact
            ? $"已选择：{contact.DisplayName}  ·  内部 username：{contact.Username}"
            : "尚未选择联系人。请单击列表中的一行；后续扫描会使用该行的内部 username。";

    [RelayCommand]
    private Task LoadContactsAsync()
    {
        var workspacePath = string.IsNullOrWhiteSpace(WorkspacePath) ? Services.Project.WorkspacePath : WorkspacePath;
        var searchTerm = string.IsNullOrWhiteSpace(SearchTerm) ? null : SearchTerm;
        return RunHost.RunAsync(
        async (context, cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(workspacePath))
            {
                throw new WeChatVoice.Core.Errors.AppFailureException(WeChatVoice.Core.Errors.ErrorCode.InvalidRequest, "Workspace path is required.");
            }

            return await Workflows.ContactDiscovery.RunAsync(
                new ContactDiscoveryRequest(workspacePath!, SearchTerm: searchTerm),
                context,
                cancellationToken).ConfigureAwait(false);
        },
        result =>
        {
            Contacts = result.Contacts;
            Services.Project.ClearVoiceSelection(clearContact: true);
            SelectedContact = null;
            Services.Project.Workspace = result.Workspace;
            WorkspacePath = result.WorkspaceDocumentPath;
            Services.Project.WorkspacePath = result.WorkspaceDocumentPath;
            AccountSummary = $"账号：{result.Workspace.DataSet.AccountId ?? "（未绑定）"}";
            ContactSummary = $"共 {result.Contacts.Count} 个一对一联系人（群聊已排除）";
        });
    }

    partial void OnSelectedContactChanged(ContactRecord? value)
    {
        Services.Project.SelectedContact = value;
        Services.Project.ClearVoiceSelection(clearContact: false);
        OnPropertyChanged(nameof(HasSelectedContact));
        OnPropertyChanged(nameof(SelectedContactSummary));
    }

    [RelayCommand]
    private void ClearSelection()
        => SelectedContact = null;

    protected override void OnProjectPropertyChanged(string? propertyName)
    {
        if (propertyName == nameof(ExportProjectSession.WorkspacePath))
        {
            WorkspacePath = Services.Project.WorkspacePath;
        }
        else if (propertyName == nameof(ExportProjectSession.SelectedContact)
            && !ReferenceEquals(SelectedContact, Services.Project.SelectedContact))
        {
            SelectedContact = Services.Project.SelectedContact;
        }
    }
}
