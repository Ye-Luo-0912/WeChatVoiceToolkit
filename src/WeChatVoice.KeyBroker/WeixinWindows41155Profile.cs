using System.Security.Cryptography;
using WeChatVoice.Core.Models;
using WeChatVoice.KeyAcquisition.Models;
using WeChatVoice.KeyAcquisition.Ports;
using WeChatVoice.KeyAcquisition.Validation;
using WeChatVoice.Windows;

namespace WeChatVoice.KeyBroker;

/// <summary>
/// Candidate acquisition for the observed signed Weixin 4.1.11.55 build.
/// It validates a candidate independently against every encrypted database
/// group in the verified Snapshot. It is intentionally not in the formal
/// registry until a plaintext materializer is installed.
/// </summary>
internal sealed class WeixinWindows41155Profile(
    IDatabaseKeyValidator validator,
    IWeixinProcessMemorySourceFactory memorySourceFactory,
    Action<ProcessMemoryScanResult>? progress = null) : IWeixinKeyExtractionProfile
{
    internal const string SupportedVersion = "4.1.11.55";
    internal const string SupportedImageSha256 = "ac599744a7ce7b65640ebe18c939c0d4e4a06cd039d89cddee7f1e9afc56875d";

    private readonly IDatabaseKeyValidator validator = validator ?? throw new ArgumentNullException(nameof(validator));
    private readonly IWeixinProcessMemorySourceFactory memorySourceFactory = memorySourceFactory ?? throw new ArgumentNullException(nameof(memorySourceFactory));
    private readonly Action<ProcessMemoryScanResult>? scanProgress = progress;

    // This identifier describes how a key is located and validated in the
    // signed Weixin build. The database cipher is a separate descriptor value
    // so another Weixin build can reuse the same SQLCipher materializer.
    public string Id => "weixin-windows-4.1.11.55-wcdb-ascii-key-v1";

    public WeixinKeyExtractionProfileDescriptor Descriptor { get; } = new(
        new HashSet<string>(StringComparer.Ordinal) { SupportedVersion },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SupportedImageSha256 },
        "weixin-windows-4.sqlcipher4-page-hmac-sha512-v1",
        "x64",
        ProfileMaturity.ExperimentalLive);

    public async Task<IReadOnlyList<ValidatedDatabaseKey>> AcquireAsync(
        VerifiedWeixinProcess process,
        VerifiedRawSnapshot snapshot,
        KeyAcquisitionBudget budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(budget);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Descriptor.ProductVersions.Contains(process.ProductVersion)
            || !Descriptor.ImageSha256.Contains(process.ImageSha256)
            || !string.Equals(process.Architecture, Descriptor.Architecture, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The verified process does not match the 4.1.11.55 Profile.");
        }

        var targets = await DatabaseGroupTarget.LoadAsync(snapshot, cancellationToken).ConfigureAwait(false);
        if (targets.Count == 0)
        {
            throw new InvalidDataException("The verified Snapshot contains no database groups for Profile validation.");
        }

        var validated = new Dictionary<string, ValidatedDatabaseKey>(StringComparer.Ordinal);
        using var source = memorySourceFactory.Open(process);
        using var scanner = new HexKeyCandidateScanner((candidate, _) =>
        {
            foreach (var target in targets)
            {
                if (validated.ContainsKey(target.DatabaseGroupFingerprint))
                {
                    continue;
                }

                if (!validator.ValidateFirstPage(target.FirstPage, candidate).IsValid)
                {
                    continue;
                }

                var key = new SensitiveBuffer(candidate.Length);
                key.CopyFrom(candidate);
                validated.Add(target.DatabaseGroupFingerprint, new ValidatedDatabaseKey(
                    target.DatabaseGroupFingerprint,
                    Id,
                    key,
                    target.SourceRelativePath,
                    target.LogicalRole,
                    target.ShardNumber));
            }

            return validated.Count != targets.Count;
        }, budget.MaximumCandidates);

        var scan = source.Scan(scanner.ProcessChunk, budget, cancellationToken);
        scanProgress?.Invoke(scan with { CandidateCount = scanner.CandidateCount });
        if (validated.Count != targets.Count)
        {
            foreach (var item in validated.Values)
            {
                item.KeyMaterial.Dispose();
            }

            throw new InvalidDataException($"The 4.1.11.55 Profile validated {validated.Count} of {targets.Count} database groups; materialization is refused.");
        }

        return validated.Values.ToArray();
    }
}

internal interface IWeixinProcessMemorySourceFactory
{
    IWeixinProcessMemorySource Open(VerifiedWeixinProcess process);
}

internal interface IWeixinProcessMemorySource : IDisposable
{
    ProcessMemoryScanResult Scan(ProcessMemoryChunkHandler handler, KeyAcquisitionBudget budget, CancellationToken cancellationToken);
}

internal sealed class WindowsWeixinProcessMemorySourceFactory : IWeixinProcessMemorySourceFactory
{
    public IWeixinProcessMemorySource Open(VerifiedWeixinProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var identity = new WeixinProcessMemoryIdentity(
            new WeChatProcessInfo(process.ProcessId, "Weixin"),
            process.ImagePath,
            process.StartedAtUtc.UtcDateTime,
            process.SessionId);
        return new WindowsWeixinProcessMemorySource(
            WeixinProcessMemorySession.TryOpen(identity)
                ?? throw new UnauthorizedAccessException("The verified Weixin process could not be opened with read-only rights."));
    }
}

internal sealed class WindowsWeixinProcessMemorySource(WeixinProcessMemorySession session) : IWeixinProcessMemorySource
{
    private readonly WeixinProcessMemorySession session = session ?? throw new ArgumentNullException(nameof(session));

    public ProcessMemoryScanResult Scan(ProcessMemoryChunkHandler handler, KeyAcquisitionBudget budget, CancellationToken cancellationToken) =>
        session.ScanReadableMemory(
            handler,
            new ProcessMemoryScanBudget(budget.MaximumDuration, budget.MaximumScanBytes),
            cancellationToken);

    public void Dispose() => session.Dispose();
}

internal sealed record DatabaseGroupTarget(
    string DatabaseGroupFingerprint,
    byte[] FirstPage,
    string SourceRelativePath,
    string LogicalRole,
    int? ShardNumber)
{
    internal static async Task<IReadOnlyList<DatabaseGroupTarget>> LoadAsync(
        VerifiedRawSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var root = snapshot.Snapshot.SnapshotDirectory;
        var targets = new List<DatabaseGroupTarget>();
        foreach (var file in snapshot.Snapshot.Manifest.Files
            .Where(static item => item.RelativePath.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static item => item.RelativePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            {
                throw new InvalidDataException("The Profile database target escaped or disappeared from the verified Snapshot.");
            }

            var firstPage = new byte[WeixinWindows4SqlCipherKeyValidator.PageSize];
            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, firstPage.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var read = 0;
            while (read < firstPage.Length)
            {
                var count = await stream.ReadAsync(firstPage.AsMemory(read), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                read += count;
            }

            if (read != firstPage.Length)
            {
                CryptographicOperations.ZeroMemory(firstPage);
                throw new InvalidDataException($"Database group '{file.RelativePath}' is smaller than one encrypted page.");
            }

            var (logicalRole, shardNumber) = Classify(Path.GetFileName(file.RelativePath));
            var groupFingerprint = ComputeGroupFingerprint(snapshot.Snapshot.Manifest.Files, file, logicalRole, shardNumber);
            targets.Add(new DatabaseGroupTarget(groupFingerprint, firstPage, file.RelativePath, logicalRole, shardNumber));
        }

        return targets;
    }

    private static string ComputeGroupFingerprint(
        IReadOnlyList<SnapshotFileRecord> files,
        SnapshotFileRecord main,
        string logicalRole,
        int? shardNumber)
    {
        var basePath = main.RelativePath.Replace('\\', '/');
        var group = files.Where(item => item.RelativePath.Replace('\\', '/').Equals(basePath, StringComparison.OrdinalIgnoreCase)
            || item.RelativePath.Replace('\\', '/').Equals(basePath + "-wal", StringComparison.OrdinalIgnoreCase)
            || item.RelativePath.Replace('\\', '/').Equals(basePath + "-shm", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .Select(item => item);
        var canonical = string.Join('|',
            basePath,
            logicalRole,
            shardNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            main.ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            main.Sha256.ToLowerInvariant(),
            group.FirstOrDefault(item => item.RelativePath.Replace('\\', '/').Equals(basePath + "-wal", StringComparison.OrdinalIgnoreCase))?.ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            group.FirstOrDefault(item => item.RelativePath.Replace('\\', '/').Equals(basePath + "-wal", StringComparison.OrdinalIgnoreCase))?.Sha256.ToLowerInvariant() ?? string.Empty,
            group.FirstOrDefault(item => item.RelativePath.Replace('\\', '/').Equals(basePath + "-shm", StringComparison.OrdinalIgnoreCase))?.ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            group.FirstOrDefault(item => item.RelativePath.Replace('\\', '/').Equals(basePath + "-shm", StringComparison.OrdinalIgnoreCase))?.Sha256.ToLowerInvariant() ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static (string Role, int? Shard) Classify(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        if (TryParseShard(lower, "message", out var messageShard))
        {
            return ("message", messageShard);
        }

        if (TryParseShard(lower, "media", out var mediaShard))
        {
            return ("media", mediaShard);
        }

        return lower.Contains("contact", StringComparison.Ordinal) || lower.Contains("friend", StringComparison.Ordinal)
            ? ("contact", ParseOptionalShard(lower))
            : ("unknown", ParseOptionalShard(lower));
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
        return underscore >= 0 && int.TryParse(stem[(underscore + 1)..], out var parsed) && parsed >= 0 ? parsed : null;
    }
}
