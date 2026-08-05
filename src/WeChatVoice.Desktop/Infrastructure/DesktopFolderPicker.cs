using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace WeChatVoice.Desktop.Infrastructure;

/// <summary>Desktop-host folder picker; page ViewModels depend on this port.</summary>
public interface IDesktopFolderPicker
{
    void Attach(TopLevel owner);

    Task<string?> PickFolderAsync(string title, CancellationToken cancellationToken = default);
}

public sealed class DesktopFolderPicker : IDesktopFolderPicker
{
    private TopLevel? _owner;

    public void Attach(TopLevel owner) => _owner = owner ?? throw new ArgumentNullException(nameof(owner));

    public async Task<string?> PickFolderAsync(string title, CancellationToken cancellationToken = default)
    {
        var provider = _owner?.StorageProvider
            ?? throw new InvalidOperationException("The Desktop folder picker is not attached to a window.");
        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        }).ConfigureAwait(true);
        return folders.Count == 0 ? null : folders[0].Path.LocalPath;
    }
}
