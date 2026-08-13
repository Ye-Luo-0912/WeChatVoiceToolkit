using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using WeChatVoice.Core.Models;

namespace WeChatVoice.Infrastructure.SeedVc;

/// <summary>
/// Performs a bounded, read-only SSH probe of the configured Linux toolchain.
/// It never reads private keys, uploads files, or starts Seed-VC. The OpenSSH
/// alias is resolved by the user's normal SSH configuration.
/// </summary>
public sealed class SeedVcRemoteProbeService
{
    private const int MaximumOutputCharacters = 32 * 1024;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    public async Task<SeedVcRemoteProbeReport> ProbeAsync(
        SeedVcToolchainResolution resolution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        var checkedAt = DateTimeOffset.UtcNow;
        var host = resolution.LinuxHost?.Trim();
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(host))
        {
            return new SeedVcRemoteProbeReport(false, false, null, null, null, null, false,
                resolution.LinuxSeedVcRoot, ["linux-host-missing"], checkedAt);
        }

        var python = string.IsNullOrWhiteSpace(resolution.LinuxPythonPath) ? "python3" : resolution.LinuxPythonPath!;
        var ffmpeg = string.IsNullOrWhiteSpace(resolution.LinuxFfmpegPath) ? "ffmpeg" : resolution.LinuxFfmpegPath!;
        var root = resolution.LinuxSeedVcRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            issues.Add("linux-seedvc-root-missing");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ssh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };
        process.StartInfo.ArgumentList.Add("-o");
        process.StartInfo.ArgumentList.Add("BatchMode=yes");
        process.StartInfo.ArgumentList.Add("-o");
        process.StartInfo.ArgumentList.Add("ConnectTimeout=8");
        process.StartInfo.ArgumentList.Add("-o");
        process.StartInfo.ArgumentList.Add("ConnectionAttempts=1");
        if (resolution.LinuxPort is > 0 and <= 65535)
        {
            process.StartInfo.ArgumentList.Add("-p");
            process.StartInfo.ArgumentList.Add(resolution.LinuxPort.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        process.StartInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(resolution.LinuxUser)
            ? host
            : $"{resolution.LinuxUser}@{host}");
        process.StartInfo.ArgumentList.Add(BuildProbeCommand(root, python, ffmpeg));

        try
        {
            if (!process.Start())
            {
                issues.Add("ssh-start-failed");
                return EmptyReport(resolution, checkedAt, host, issues);
            }

            var stdoutTask = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            var stderrTask = ReadBoundedAsync(process.StandardError, timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                issues.Add("linux-ssh-failed");
            }

            var values = Parse(stdout);
            var reachable = process.ExitCode == 0 && values.ContainsKey("platform");
            if (!reachable && !issues.Contains("linux-ssh-failed", StringComparer.Ordinal))
            {
                issues.Add("linux-ssh-failed");
            }

            var seedVcFound = string.Equals(values.GetValueOrDefault("seedvc"), "ready", StringComparison.OrdinalIgnoreCase);
            if (reachable && !seedVcFound && !issues.Contains("linux-seedvc-root-missing", StringComparer.Ordinal))
            {
                issues.Add("seedvc-files-missing");
            }

            return new SeedVcRemoteProbeReport(
                IsReady: reachable && seedVcFound && issues.Count == 0,
                IsReachable: reachable,
                Host: host,
                Platform: values.GetValueOrDefault("platform"),
                PythonVersion: values.GetValueOrDefault("python"),
                FfmpegVersion: values.GetValueOrDefault("ffmpeg"),
                SeedVcFound: seedVcFound,
                SeedVcRoot: root,
                Issues: issues,
                CheckedAtUtc: checkedAt);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            issues.Add("linux-ssh-timeout");
            return EmptyReport(resolution, checkedAt, host, issues);
        }
        catch (Win32Exception)
        {
            issues.Add("ssh-missing");
            return EmptyReport(resolution, checkedAt, host, issues);
        }
        catch
        {
            issues.Add("linux-ssh-failed");
            return EmptyReport(resolution, checkedAt, host, issues);
        }
    }

    internal static string BuildProbeCommand(string? root, string python, string ffmpeg)
    {
        var quotedPython = QuoteForPosixShell(python);
        var quotedFfmpeg = QuoteForPosixShell(ffmpeg);
        var quotedRoot = QuoteForPosixShell(root ?? string.Empty);
        var script = $"printf 'platform='; uname -srm; printf 'python='; {quotedPython} --version 2>&1; printf 'ffmpeg='; {quotedFfmpeg} -version 2>&1 | head -n 1; if [ -n {quotedRoot} ] && [ -f {quotedRoot}/train.py ] && [ -f {quotedRoot}/app_vc.py ]; then printf 'seedvc=ready\\n'; else printf 'seedvc=missing\\n'; fi";
        // Weixin hosts may use fish (or another non-POSIX login shell). Force
        // the fixed probe through /bin/sh so the condition syntax is stable.
        return $"sh -c {QuoteForPosixShell(script)}";
    }

    internal static string QuoteForPosixShell(string value)
        => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[2048];
        var output = new StringBuilder();
        while (output.Length < MaximumOutputCharacters)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            var take = Math.Min(count, MaximumOutputCharacters - output.Length);
            output.Append(buffer, 0, take);
            if (take < count)
            {
                break;
            }
        }

        return output.ToString();
    }

    private static Dictionary<string, string> Parse(string output)
        => output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => Limit(parts[1].Trim()), StringComparer.OrdinalIgnoreCase);

    private static string Limit(string value) => value.Length <= 160 ? value : value[..160];

    private static SeedVcRemoteProbeReport EmptyReport(SeedVcToolchainResolution resolution, DateTimeOffset checkedAt, string host, List<string> issues)
        => new(false, false, host, null, null, null, false, resolution.LinuxSeedVcRoot, issues, checkedAt);

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort only; no process details are exposed to the caller.
        }
    }
}
