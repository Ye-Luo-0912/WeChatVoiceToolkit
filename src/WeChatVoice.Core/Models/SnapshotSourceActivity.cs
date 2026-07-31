using System.Collections.ObjectModel;

namespace WeChatVoice.Core.Models;

/// <summary>
/// A point-in-time observation of source processes that may mutate a database
/// file set while it is being copied.
/// </summary>
public sealed record SnapshotSourceActivity
{
    public SnapshotSourceActivity(bool IsLive, IEnumerable<string>? ProcessNames = null)
    {
        this.IsLive = IsLive;
        this.ProcessNames = new ReadOnlyCollection<string>(
            (ProcessNames ?? Array.Empty<string>())
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    public bool IsLive { get; }

    public IReadOnlyList<string> ProcessNames { get; }
}
