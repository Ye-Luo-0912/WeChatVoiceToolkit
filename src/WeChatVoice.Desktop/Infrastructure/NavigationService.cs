namespace WeChatVoice.Desktop.Infrastructure;

/// <summary>
/// Lightweight page-navigation bridge used by page view models. The Desktop
/// navigation host (MainWindowViewModel) subscribes to <see cref="NavigationRequested"/>
/// and maps each requested page type to the corresponding page in its Pages
/// collection. Page view models never re-implement navigation; they only raise
/// an intent.
/// </summary>
public interface INavigationService
{
    event Action<Type>? NavigationRequested;

    /// <summary>Raises a navigation intent for the given page type.</summary>
    void NavigateTo(Type pageType);
}

/// <summary>Default <see cref="INavigationService"/> implementation.</summary>
public sealed class NavigationService : INavigationService
{
    public event Action<Type>? NavigationRequested;

    public void NavigateTo(Type pageType)
    {
        ArgumentNullException.ThrowIfNull(pageType);
        NavigationRequested?.Invoke(pageType);
    }
}