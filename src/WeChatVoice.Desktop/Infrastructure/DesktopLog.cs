using System.Text.RegularExpressions;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;

namespace WeChatVoice.Desktop.Infrastructure;

/// <summary>
/// LocalApplicationData log for the Desktop host. The log is deliberately
/// narrow: it records stages, error codes, and durations only. It never
/// receives contact usernames, keys, memory contents, or database data; a
/// defensive scrubber additionally redacts wxid_ identifiers and long hex
/// values in case a caller misuses the API.
/// </summary>
public sealed partial class DesktopLog
{
    private static readonly object Gate = new();
    private readonly string _directory;

    public DesktopLog(string? directory = null)
        => _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WeChatVoiceToolkit",
            "logs");

    /// <summary>Path of today's log file.</summary>
    public string LogPath => Path.Combine(_directory, $"desktop-{DateTime.UtcNow:yyyyMMdd}.log");

    /// <summary>Last lines for the diagnostics page (ring buffer of the session).</summary>
    public IReadOnlyList<string> RecentLines { get; } = new RingBuffer();

    public void Stage(OperationPhase phase, string stageId, double? percent = null)
        => Write($"stage {phase}:{Scrub(stageId)}{(percent is { } p ? $" {p:0}%" : string.Empty)}");

    public void Info(string message) => Write($"info {Scrub(message)}");

    public void Error(string message) => Write($"error {Scrub(message)}");

    /// <summary>Records a typed failure code; no exception text crosses the log.</summary>
    public void ErrorCode(ErrorCode code) => Write($"error-code {code}");

    public void ErrorCode(BrokerTransportErrorCode code) => Write($"transport-code {code}");

    private void Write(string line)
    {
        var entry = $"{DateTime.UtcNow:O} {line}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllText(LogPath, entry + Environment.NewLine);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        if (RecentLines is RingBuffer ring)
        {
            ring.Add(entry);
        }
    }

    internal static string Scrub(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        value = WxidPattern().Replace(value, "wxid_***");
        value = HexPattern().Replace(value, "***");
        return value;
    }

    [GeneratedRegex("wxid_[a-zA-Z0-9_]+", RegexOptions.IgnoreCase)]
    private static partial Regex WxidPattern();

    [GeneratedRegex("\\b[0-9a-fA-F]{32,}\\b")]
    private static partial Regex HexPattern();

    private sealed class RingBuffer : List<string>
    {
        private const int MaxCapacity = 200;

        public new void Add(string item)
        {
            lock (Gate)
            {
                base.Add(item);
                if (Count > MaxCapacity)
                {
                    RemoveRange(0, Count - MaxCapacity);
                }
            }
        }
    }
}
