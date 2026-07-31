using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Infrastructure.Sqlite;

/// <summary>
/// Discovers a decrypted SQLite bundle without assigning product semantics to
/// unknown tables. Filename roles are limited to the documented message/media
/// and contact artifact patterns; adapter selection remains schema-driven.
/// </summary>
public sealed class DataSetProbeService
{
    private readonly SqliteSchemaInspector _schemaInspector;
    private readonly IReadOnlyList<IWeChatDataSetAdapter> _adapters;

    public DataSetProbeService(
        SqliteSchemaInspector? schemaInspector = null,
        IEnumerable<IWeChatDataSetAdapter>? adapters = null)
    {
        _schemaInspector = schemaInspector ?? new SqliteSchemaInspector();
        _adapters = (adapters ?? Array.Empty<IWeChatDataSetAdapter>()).ToArray();
    }

    public async Task<DataSetProbe> ProbeAsync(
        string rootDirectory,
        DataSetProbeOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        options ??= new DataSetProbeOptions();
        cancellationToken.ThrowIfCancellationRequested();

        var root = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"The data-set root was not found: '{root}'.");
        }

        var issues = new List<DataSetIssue>();
        var files = Directory.EnumerateFiles(root, "*.db", SearchOption.AllDirectories)
            .Where(static path => !IsInsideReparsePoint(path))
            .Select(path => new DiscoveredDatabase(path, NormalizeRelativePath(root, path), Classify(Path.GetFileName(path))))
            .OrderBy(static item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var artifacts = new List<DatabaseArtifact>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localPath = file.FullPath;
            var walPath = localPath + "-wal";
            var shmPath = localPath + "-shm";
            var walPresent = File.Exists(walPath);
            var shmPresent = File.Exists(shmPath);
            var completenessIssue = walPresent == shmPresent ? null : "SQLite WAL and SHM sidecars are not present as a complete pair.";
            if (completenessIssue is not null)
            {
                issues.Add(new DataSetIssue("incomplete-wal-pair", "warning", completenessIssue, file.RelativePath));
            }

            var hash = await FileHashing.ComputeSha256Async(localPath, cancellationToken).ConfigureAwait(false);
            SchemaSnapshot schema;
            try
            {
                schema = await _schemaInspector.InspectAsync(
                    localPath,
                    new SchemaInspectionOptions(options.IncludeLocalPaths, walPath, shmPath),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SqliteException or InvalidDataException or IOException)
            {
                issues.Add(new DataSetIssue("schema-probe-failed", "error", exception.Message, file.RelativePath));
                schema = new SchemaSnapshot(
                    options.IncludeLocalPaths ? localPath : Path.GetFileName(localPath),
                    DateTimeOffset.UtcNow,
                    DatabaseSha256: hash,
                    FileCompleteness: new SchemaFileCompleteness(walPresent, shmPresent, completenessIssue is null, completenessIssue),
                    LocalPath: options.IncludeLocalPaths ? localPath : null);
            }

            artifacts.Add(new DatabaseArtifact(
                file.LogicalRole,
                file.ShardNumber,
                file.RelativePath,
                hash,
                schema,
                options.IncludeLocalPaths ? localPath : null,
                walPresent,
                shmPresent,
                completenessIssue));
        }

        AddPairingIssues(artifacts, issues);
        if (artifacts.Count == 0)
        {
            issues.Add(new DataSetIssue("no-databases", "error", "No .db files were found under the supplied root."));
        }

        var datasetId = ComputeDataSetId(artifacts);
        var dataSet = new WeChatDataSet(datasetId, null, artifacts);
        var candidates = _adapters
            .Select(adapter => (adapter, match: adapter.Probe(dataSet)))
            .Select(item => new AdapterCandidate(item.adapter.Id, item.match.IsMatch, item.match.Score, item.match.Reason))
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.AdapterId, StringComparer.Ordinal)
            .ToArray();

        return new DataSetProbe(
            dataSet,
            DateTimeOffset.UtcNow,
            issues,
            candidates,
            options.IncludeLocalPaths ? root : null,
            options.IncludeLocalPaths);
    }

    private static void AddPairingIssues(IReadOnlyList<DatabaseArtifact> artifacts, ICollection<DataSetIssue> issues)
    {
        var messageShards = artifacts.Where(static item => string.Equals(item.LogicalRole, "message", StringComparison.OrdinalIgnoreCase))
            .Select(static item => item.ShardNumber ?? 0).ToHashSet();
        var mediaShards = artifacts.Where(static item => string.Equals(item.LogicalRole, "media", StringComparison.OrdinalIgnoreCase))
            .Select(static item => item.ShardNumber ?? 0).ToHashSet();
        foreach (var shard in messageShards.Except(mediaShards).Order())
        {
            issues.Add(new DataSetIssue("missing-media-shard", "warning", $"message_{shard}.db has no matching media shard.", $"message_{shard}.db"));
        }

        foreach (var shard in mediaShards.Except(messageShards).Order())
        {
            issues.Add(new DataSetIssue("missing-message-shard", "warning", $"media_{shard}.db has no matching message shard.", $"media_{shard}.db"));
        }

        if (!artifacts.Any(static item => string.Equals(item.LogicalRole, "contact", StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new DataSetIssue("missing-contact-database", "warning", "No contact database matched the conservative contact filename patterns."));
        }
    }

    private static string ComputeDataSetId(IEnumerable<DatabaseArtifact> artifacts)
    {
        var canonical = string.Join("\n", artifacts.OrderBy(static item => item.DatabasePath, StringComparer.OrdinalIgnoreCase)
            .Select(static item => $"{item.LogicalRole}|{item.ShardNumber}|{item.DatabasePath}|{item.Sha256}|{item.Schema.SchemaFingerprint}"));
        return "dataset-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()[..16];
    }

    private static ArtifactRole Classify(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        if (TryParseShard(lower, "message", out var messageShard))
        {
            return new ArtifactRole("message", messageShard);
        }

        if (TryParseShard(lower, "media", out var mediaShard))
        {
            return new ArtifactRole("media", mediaShard);
        }

        if (lower.Contains("contact", StringComparison.Ordinal) || lower.Contains("friend", StringComparison.Ordinal))
        {
            return new ArtifactRole("contact", ParseOptionalShard(lower));
        }

        return new ArtifactRole("unknown", ParseOptionalShard(lower));
    }

    private static bool TryParseShard(string fileName, string prefix, out int? shard)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (!stem.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase))
        {
            shard = null;
            return false;
        }

        var suffix = stem[(prefix.Length + 1)..];
        if (int.TryParse(suffix, out var parsed) && parsed >= 0)
        {
            shard = parsed;
            return true;
        }

        shard = null;
        return false;
    }

    private static int? ParseOptionalShard(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var underscore = stem.LastIndexOf('_');
        return underscore >= 0 && int.TryParse(stem[(underscore + 1)..], out var shard) && shard >= 0 ? shard : null;
    }

    private static bool IsInsideReparsePoint(string path)
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(path)!);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private static string NormalizeRelativePath(string root, string path)
        => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private sealed record DiscoveredDatabase(string FullPath, string RelativePath, ArtifactRole Role)
    {
        public string LogicalRole => Role.LogicalRole;
        public int? ShardNumber => Role.ShardNumber;
    }

    private sealed record ArtifactRole(string LogicalRole, int? ShardNumber);
}
