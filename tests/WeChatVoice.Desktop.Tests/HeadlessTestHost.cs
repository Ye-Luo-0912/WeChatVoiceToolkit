using Avalonia;
using Avalonia.Headless;

namespace WeChatVoice.Desktop.Tests;

internal static class HeadlessTestHost
{
    private static readonly Lazy<HeadlessUnitTestSession> Instance = new(
        static () => HeadlessUnitTestSession.StartNew(typeof(App)));

    public static void EnsureStarted() => _ = Instance.Value;

    public static void Dispatch(Action action)
        => Instance.Value.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();

    public static T Dispatch<T>(Func<T> action)
        => Instance.Value.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();

    public static Task DispatchOnUiAsync(Action action)
        => Instance.Value.Dispatch(action, CancellationToken.None);

    public static async Task DispatchAsync(Func<Task> action)
        => await Instance.Value.Dispatch(action, CancellationToken.None).Unwrap();
}
