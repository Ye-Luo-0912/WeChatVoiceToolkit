namespace WeChatVoice.Core.Models;

using System.Collections.Frozen;

/// <summary>
/// A one-time, content-verified index of every regular file under a root.
/// Dataset probing, workspace verification, and provenance verification share
/// the same entries so a file is hashed exactly once per verification pass.
/// Keys are normalized forward-slash relative paths. The index is built once
/// and never mutated after construction.
/// </summary>
public sealed record VerifiedFileIndex
{
    public VerifiedFileIndex(IEnumerable<KeyValuePair<string, VerifiedFileEntry>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var dictionary = new Dictionary<string, VerifiedFileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in entries)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
            {
                continue;
            }

            dictionary[pair.Key.Replace('\\', '/')] = pair.Value;
        }

        Entries = dictionary.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, VerifiedFileEntry> Entries { get; }

    public bool TryGet(string relativePath, out VerifiedFileEntry entry)
    {
        if (Entries.TryGetValue(relativePath.Replace('\\', '/'), out var found) && found is not null)
        {
            entry = found;
            return true;
        }

        entry = null!;
        return false;
    }
}

public sealed record VerifiedFileEntry(
    long ByteLength,
    string Sha256,
    DateTimeOffset LastWriteTimeUtc,
    bool HasPlainSqliteHeader,
    string? FileId = null);
