using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using WeChatVoice.Core.Errors;

namespace WeChatVoice.Infrastructure.Audio;

/// <summary>
/// Optional post-processing step for decoded WAV files. FFmpeg is deliberately
/// used only with a fixed argument list and a temporary sibling output; it is
/// not used to execute arbitrary user commands. The bundled SILK decoder still
/// performs the SILK-to-PCM step because many FFmpeg builds do not expose a
/// WeChat SILK demuxer.
/// </summary>
public sealed class FfmpegWavNormalizer
{
    private const int BufferSize = 64 * 1024;
    private const int MaximumDiagnosticCharacters = 16 * 1024;
    private readonly string _ffmpegPath;

    public FfmpegWavNormalizer(string ffmpegPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        _ffmpegPath = Path.GetFullPath(ffmpegPath);
    }

    public async Task NormalizeAsync(
        string wavPath,
        int sampleRate,
        bool mono,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        var input = Path.GetFullPath(wavPath);
        if (!File.Exists(input) || !File.Exists(_ffmpegPath))
        {
            return;
        }

        var output = input + ".ffmpeg-" + Guid.NewGuid().ToString("N") + ".wav";
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                WorkingDirectory = Path.GetDirectoryName(_ffmpegPath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-loglevel");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(input);
            startInfo.ArgumentList.Add("-ar");
            startInfo.ArgumentList.Add(sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-ac");
            startInfo.ArgumentList.Add(mono ? "1" : "2");
            startInfo.ArgumentList.Add("-c:a");
            startInfo.ArgumentList.Add("pcm_s16le");
            startInfo.ArgumentList.Add(output);

            using var process = Process.Start(startInfo)
                ?? throw new AppFailureException(ErrorCode.WorkflowFailed, "FFmpeg could not be started.");
            var stdout = ReadBoundedAsync(process.StandardOutput, cancellationToken);
            var stderr = ReadBoundedAsync(process.StandardError, cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                throw;
            }
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
            {
                throw new AppFailureException(ErrorCode.WorkflowFailed, "FFmpeg could not normalize the decoded WAV.");
            }

            File.Move(output, input, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(output)) File.Delete(output);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var builder = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            var remaining = MaximumDiagnosticCharacters - builder.Length;
            if (remaining > 0) builder.Append(buffer, 0, Math.Min(remaining, read));
        }

        return builder.ToString();
    }
}

/// <summary>Returns a full FFmpeg path from PATH without starting a process.</summary>
public static class FfmpegLocator
{
    public static string? Discover(string? requested = null)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            if (Path.IsPathRooted(requested) || requested.Contains(Path.DirectorySeparatorChar) || requested.Contains(Path.AltDirectorySeparatorChar))
            {
                var full = Path.GetFullPath(requested);
                return File.Exists(full) ? full : null;
            }

            var requestedName = requested.Trim();
            var names = OperatingSystem.IsWindows() && !requestedName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? new[] { requestedName, requestedName + ".exe" }
                : new[] { requestedName };
            var requestedPath = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(directory => names.Select(name => Path.Combine(directory, name)))
                .FirstOrDefault(File.Exists);
            if (requestedPath is not null) return Path.GetFullPath(requestedPath);
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(path))
        {
            candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        // Windows package managers commonly install FFmpeg outside the
        // process PATH. Include only the user's local package roots; no broad
        // recursive disk scan is performed.
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                candidates.Add(Path.Combine(localAppData, "Microsoft", "WinGet", "Packages"));
            }
        }

        foreach (var directory in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (directory.EndsWith("Packages", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var package in Directory.EnumerateDirectories(directory, "Gyan.FFmpeg*", SearchOption.TopDirectoryOnly))
                    {
                        var bin = Path.Combine(package, "bin", "ffmpeg.exe");
                        if (File.Exists(bin)) return Path.GetFullPath(bin);
                    }
                }

                var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch (ArgumentException) { }
        }

        return null;
    }
}
