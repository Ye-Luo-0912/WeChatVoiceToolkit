using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeChatVoice.Core.Models;
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
        : base(services)
    {
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

    [RelayCommand]
    private Task LoadContactsAsync() => RunHost.RunAsync(
        async (context, cancellationToken) =>
        {
            var workspacePath = string.IsNullOrWhiteSpace(WorkspacePath) ? Services.Project.WorkspacePath : WorkspacePath;
            if (string.IsNullOrWhiteSpace(workspacePath))
            {
                throw new WeChatVoice.Core.Errors.AppFailureException(WeChatVoice.Core.Errors.ErrorCode.InvalidRequest, "Workspace path is required.");
            }

            return await Workflows.ContactDiscovery.RunAsync(
                new ContactDiscoveryRequest(workspacePath, SearchTerm: string.IsNullOrWhiteSpace(SearchTerm) ? null : SearchTerm),
                context,
                cancellationToken).ConfigureAwait(false);
        },
        result =>
        {
            Contacts = result.Contacts;
            SelectedContact = null;
            Services.Project.Workspace = result.Workspace;
            Services.Project.WorkspacePath = result.Workspace.Workspace.SourceRoot;
            AccountSummary = $"账号：{result.Workspace.DataSet.AccountId ?? "（未绑定）"}";
            ContactSummary = $"共 {result.Contacts.Count} 个一对一联系人（群聊已排除）";
        });

    partial void OnSelectedContactChanged(ContactRecord? value)
    {
        Services.Project.SelectedContact = value;
        Services.Project.Scan = null;
        Services.Project.LastExportRun = null;
    }
}
