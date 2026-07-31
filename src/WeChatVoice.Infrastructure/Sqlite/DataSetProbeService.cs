using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Adapters;
using WeChatVoice.Infrastructure.Serialization;
using WeChatVoice.Infrastructure.Snapshots;

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
        _adapters = (adapters ?? BuiltInAdapters.Create()).ToArray();
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
        var snapshotFiles = await BuildSnapshotFileLookupAsync(root, options.SnapshotManifest, cancellationToken).ConfigureAwait(false);
        var files = SnapshotFileCopier.EnumerateRegularFiles(root)
            .Where(static path => string.Equals(Path.GetExtension(path), ".db", StringComparison.OrdinalIgnoreCase))
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

            var metadata = await FileHashing.ComputeMetadataAsync(localPath, cancellationToken).ConfigureAwait(false);
            var mainRecord = snapshotFiles?.GetValueOrDefault(file.RelativePath);
            var mainLength = mainRecord?.ByteLength ?? metadata.ByteLength;
            var mainHash = mainRecord?.Sha256 ?? metadata.Sha256;
            var walLength = walPresent ? (long?)new FileInfo(walPath).Length : null;
            var shmLength = shmPresent ? (long?)new FileInfo(shmPath).Length : null;
            var walRecord = snapshotFiles?.GetValueOrDefault(file.RelativePath + "-wal");
            var shmRecord = snapshotFiles?.GetValueOrDefault(file.RelativePath + "-shm");
            var walHash = walPresent
                ? walRecord?.Sha256 ?? await FileHashing.ComputeSha256Async(walPath, cancellationToken).ConfigureAwait(false)
                : null;
            var shmHash = shmPresent
                ? shmRecord?.Sha256 ?? await FileHashing.ComputeSha256Async(shmPath, cancellationToken).ConfigureAwait(false)
                : null;
            var groupFingerprint = ComputeDatabaseGroupFingerprint(
                file.RelativePath,
                file.LogicalRole,
                file.ShardNumber,
                mainLength,
                mainHash,
                walLength,
                walHash,
                shmLength,
                shmHash);
            SchemaSnapshot schema;
            if (!metadata.HasPlainSqliteHeader)
            {
                const string message = "The database does not have a plain SQLite header; it may be encrypted or use a proprietary container. No decryption is attempted.";
                issues.Add(new DataSetIssue("encrypted-or-non-sqlite", "error", message, file.RelativePath));
                schema = CreateUnavailableSchema(localPath, mainHash, options, walPresent, shmPresent, completenessIssue);
            }
            else
            {
                try
                {
                    schema = await _schemaInspector.InspectAsync(
                        localPath,
                        new SchemaInspectionOptions(options.IncludeLocalPaths, walPath, shmPath, mainHash, mainLength),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is SqliteException or InvalidDataException or IOException)
                {
                    issues.Add(new DataSetIssue("schema-probe-failed", "error", exception.Message, file.RelativePath));
                    schema = CreateUnavailableSchema(localPath, mainHash, options, walPresent, shmPresent, completenessIssue);
                }
            }

            artifacts.Add(new DatabaseArtifact(
                file.LogicalRole,
                file.ShardNumber,
                file.RelativePath,
                mainHash,
                schema,
                options.IncludeLocalPaths ? localPath : null,
                walPresent,
                shmPresent,
                completenessIssue,
                mainLength,
                walHash,
                walLength,
                shmHash,
                shmLength,
                groupFingerprint));
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

    public Task<DataSetProbe> ProbeAsync(
        VerifiedRawSnapshot snapshot,
        DataSetProbeOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // The raw boundary has already verified the manifest against the
        // current directory. Re-probing intentionally does not trust or
        // re-read a JSON manifest a second time.
        return ProbeAsync(snapshot.Snapshot.SnapshotDirectory, options, cancellationToken);
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
            .Select(static item => $"{item.LogicalRole}|{item.ShardNumber}|{item.DatabasePath}|{item.DatabaseGroupFingerprint}|{item.Schema.SchemaFingerprint}"));
        return "dataset-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()[..16];
    }

    private static async Task<IReadOnlyDictionary<string, SnapshotFileRecord>?> BuildSnapshotFileLookupAsync(
        string root,
        SnapshotManifest? snapshotManifest,
        CancellationToken cancellationToken)
    {
        if (snapshotManifest is null)
        {
            return null;
        }

        var manifestRoot = Path.GetFullPath(snapshotManifest.SnapshotDirectory);
        if (!string.Equals(root, manifestRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A SnapshotManifest may only be used against its verified SnapshotDirectory, not the original source directory.");
        }

        var expected = snapshotManifest.Files.ToDictionary(
            static file => file.RelativePath.Replace('\\', '/'),
            static file => file,
            StringComparer.OrdinalIgnoreCase);
        var actual = SnapshotFileCopier.EnumerateRegularFiles(root)
            .Where(path => !IsInternalMetadataPath(root, path))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actual.SetEquals(expected.Keys))
        {
            throw new InvalidDataException("SnapshotManifest file set does not match the verified snapshot directory.");
        }

        foreach (var pair in expected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = CombineUnderRoot(root, pair.Key);
            var info = new FileInfo(path);
            var hash = await FileHashing.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
            if (info.Length != pair.Value.ByteLength
                || !string.Equals(hash, pair.Value.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Snapshot content does not match its manifest: '{pair.Key}'.");
            }
        }

        return expected;
    }

    private static bool IsInternalMetadataPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return relative.Equals(".wechatvoice", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith(".wechatvoice/", StringComparison.OrdinalIgnoreCase);
    }

    private static string CombineUnderRoot(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("SnapshotManifest contains an absolute file path.");
        }

        var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("SnapshotManifest contains a path outside its snapshot directory.");
        }

        return candidate;
    }

    private static string ComputeDatabaseGroupFingerprint(
        string relativePath,
        string logicalRole,
        int? shardNumber,
        long mainLength,
        string mainHash,
        long? walLength,
        string? walHash,
        long? shmLength,
        string? shmHash)
    {
        var canonical = string.Join('|',
            relativePath,
            logicalRole,
            shardNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            mainLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            mainHash,
            walLength?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            walHash ?? string.Empty,
            shmLength?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            shmHash ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static SchemaSnapshot CreateUnavailableSchema(
        string localPath,
        string hash,
        DataSetProbeOptions options,
        bool walPresent,
        bool shmPresent,
        string? completenessIssue)
        => new(
            options.IncludeLocalPaths ? localPath : Path.GetFileName(localPath),
            DateTimeOffset.UtcNow,
            DatabaseSha256: hash,
            FileCompleteness: new SchemaFileCompleteness(walPresent, shmPresent, completenessIssue is null, completenessIssue),
            LocalPath: options.IncludeLocalPaths ? localPath : null);

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

    private static string NormalizeRelativePath(string root, string path)
        => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private sealed record DiscoveredDatabase(string FullPath, string RelativePath, ArtifactRole Role)
    {
        public string LogicalRole => Role.LogicalRole;
        public int? ShardNumber => Role.ShardNumber;
    }

    private sealed record ArtifactRole(string LogicalRole, int? ShardNumber);
}
