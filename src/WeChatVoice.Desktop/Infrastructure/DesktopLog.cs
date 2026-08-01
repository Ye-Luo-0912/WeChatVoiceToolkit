using System.Text.RegularExpressions;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;

namespace WeChatVoice.Desktop.Infrastructure;

/// <summary>
/// LocalApplicationData log for the Desktop host. It records stages, error
/// codes, and durations only. RecentLines is an atomic snapshot rather than a
/// live mutable collection, so the diagnostics page cannot enumerate while a
/// worker appends a new entry.
/// </summary>
public sealed partial class DesktopLog
{
    private static readonly object Gate = new();
    private readonly string _directory;
    private readonly RingBuffer _recentLines = new();

    public DesktopLog(string? directory = null)
        => _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WeChatVoiceToolkit",
            "logs");

    public string LogPath => Path.Combine(_directory, $"desktop-{DateTime.UtcNow:yyyyMMdd}.log");

    /// <summary>Returns a stable copy of the current session log.</summary>
    public IReadOnlyList<string> GetRecentSnapshot()
    {
        lock (Gate)
        {
            return _recentLines.ToArray();
        }
    }

    public IReadOnlyList<string> RecentLines => GetRecentSnapshot();

    public void Stage(OperationPhase phase, string stageId, double? percent = null)
        => Write($"stage {phase}:{Scrub(stageId)}{(percent is { } p ? $" {p:0}%" : string.Empty)}");

    public void Info(string message) => Write($"info {Scrub(message)}");

    public void Error(string message) => Write($"error {Scrub(message)}");

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

            _recentLines.AddUnsafe(entry);
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

    private sealed class RingBuffer
    {
        private const int MaxCapacity = 200;
        private readonly List<string> _items = [];

        public void AddUnsafe(string item)
        {
            _items.Add(item);
            if (_items.Count > MaxCapacity)
            {
                _items.RemoveRange(0, _items.Count - MaxCapacity);
            }
        }

        public string[] ToArray() => _items.ToArray();
    }
}
