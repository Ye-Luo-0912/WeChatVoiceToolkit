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
    private Task LoadContactsAsync() => RunHost.RunAsync(async (context, cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(WorkspacePath))
        {
            throw new ArgumentException("请选择 Workspace 路径。");
        }

        var result = await Workflows.ContactDiscovery.RunAsync(
            new ContactDiscoveryRequest(WorkspacePath, SearchTerm: string.IsNullOrWhiteSpace(SearchTerm) ? null : SearchTerm),
            context,
            cancellationToken).ConfigureAwait(false);
        Contacts = result.Contacts;
        SelectedContact = result.Contacts.FirstOrDefault();
        AccountSummary = $"账号：{result.Workspace.DataSet.AccountId ?? "（未绑定）"}";
        ContactSummary = $"共 {result.Contacts.Count} 个一对一联系人（群聊已排除）";
    });
}
