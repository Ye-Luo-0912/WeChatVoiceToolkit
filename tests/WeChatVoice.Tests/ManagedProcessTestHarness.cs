using System.Diagnostics;

namespace WeChatVoice.Tests;

internal static class ManagedProcessTestHarness
{
    internal static async Task<ManagedProcessResult> RunAssemblyAsync(
        string assemblyFileName,
        string? standardInput,
        params string[] arguments)
    {
        var assemblyPath = Path.Combine(AppContext.BaseDirectory, assemblyFileName);
        Assert.True(File.Exists(assemblyPath), $"Expected test dependency '{assemblyFileName}' was not copied to '{AppContext.BaseDirectory}'.");

        var processStart = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        processStart.ArgumentList.Add(assemblyPath);
        foreach (var argument in arguments)
        {
            processStart.ArgumentList.Add(argument);
        }

        using var process = Process.Start(processStart)
            ?? throw new InvalidOperationException($"Could not start '{assemblyFileName}'.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput).ConfigureAwait(false);
        }

        process.StandardInput.Close();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw new TimeoutException($"'{assemblyFileName}' did not exit within 30 seconds.");
        }

        return new ManagedProcessResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }
}

internal sealed record ManagedProcessResult(int ExitCode, string StandardOutput, string StandardError);
