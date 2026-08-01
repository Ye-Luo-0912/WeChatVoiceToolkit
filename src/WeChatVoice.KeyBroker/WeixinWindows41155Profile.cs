using System.Diagnostics;
using System.Security.Cryptography;
using WeChatVoice.Core.Errors;
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
    IWeixinModuleIdentityVerifier moduleIdentityVerifier,
    Action<WeixinKeyScanProgress>? progress = null) : IWeixinProcessTreeKeyExtractionProfile
{
    internal const string SupportedVersion = "4.1.11.55";
    internal const string SupportedImageSha256 = "ac599744a7ce7b65640ebe18c939c0d4e4a06cd039d89cddee7f1e9afc56875d";
    internal const string SupportedWcdbModuleSha256 = "ab925b9428239def44b252d970c337034d75e66b27eb5529633dc10669fc796a";

    // Extracted from the memory-protection routine in the signed
    // Weixin.dll above. SQLCipher applies this 32-byte sequence repeatedly
    // to raw cipher specs retained in memory. It is profile metadata, not a
    // database key.
    internal static ReadOnlySpan<byte> WcdbMemoryProtectionMask =>
    [
        0x55, 0xe8, 0x9c, 0x9f, 0xcc, 0x23, 0xe3, 0x48,
        0x2f, 0x46, 0x54, 0xd4, 0xf9, 0xd7, 0x23, 0x7e,
        0x1a, 0xcc, 0x83, 0xe5, 0xca, 0xd1, 0x41, 0x3c,
        0x7f, 0xc6, 0x59, 0xcb, 0x2a, 0x33, 0xad, 0xaf,
    ];

    private readonly IDatabaseKeyValidator validator = validator ?? throw new ArgumentNullException(nameof(validator));
    private readonly IWeixinProcessMemorySourceFactory memorySourceFactory = memorySourceFactory ?? throw new ArgumentNullException(nameof(memorySourceFactory));
    private readonly IWeixinModuleIdentityVerifier moduleIdentityVerifier = moduleIdentityVerifier ?? throw new ArgumentNullException(nameof(moduleIdentityVerifier));
    private readonly Action<WeixinKeyScanProgress>? scanProgress = progress;

    // This identifier describes how a key is located and validated in the
    // signed Weixin build. The database cipher is a separate descriptor value
    // so another Weixin build can reuse the same SQLCipher materializer.
    public string Id => "weixin-windows-4.1.11.55-wcdb-protected-spec-v2";

    public WeixinKeyExtractionProfileDescriptor Descriptor { get; } = new(
        new HashSet<string>(StringComparer.Ordinal) { SupportedVersion },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SupportedImageSha256 },
        "weixin-windows-4.1.11.55.sqlcipher-exact-set-v1",
        "x64",
        ProfileMaturity.LiveValidated);

    public async Task<IReadOnlyList<ValidatedDatabaseKey>> AcquireAsync(
        VerifiedWeixinProcess process,
        VerifiedRawSnapshot snapshot,
        KeyAcquisitionBudget budget,
        CancellationToken cancellationToken) => await AcquireAsync([process], snapshot, budget, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<ValidatedDatabaseKey>> AcquireAsync(
        IReadOnlyList<VerifiedWeixinProcess> processes,
        VerifiedRawSnapshot snapshot,
        KeyAcquisitionBudget budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(budget);
        if (processes.Count == 0)
        {
            throw new ArgumentException("At least one verified Weixin process is required.", nameof(processes));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (processes.Any(process => !Descriptor.ProductVersions.Contains(process.ProductVersion)))
        {
            throw new AppFailureException(ErrorCode.UnsupportedWeixinVersion, "A verified process does not match the 4.1.11.55 version.");
        }

        if (processes.Any(process => !Descriptor.ImageSha256.Contains(process.ImageSha256)
            || !string.Equals(process.Architecture, Descriptor.Architecture, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AppFailureException(ErrorCode.ProcessIdentityMismatch, "A verified process does not match the 4.1.11.55 Profile.");
        }

        await moduleIdentityVerifier.VerifyAsync(processes, cancellationToken).ConfigureAwait(false);

        var targets = await DatabaseGroupTarget.LoadAsync(snapshot, cancellationToken).ConfigureAwait(false);
        if (targets.Count == 0)
        {
            throw new AppFailureException(ErrorCode.SnapshotInvalid, "The verified Snapshot contains no database groups for Profile validation.");
        }

        var validated = new Dictionary<string, ValidatedDatabaseKey>(StringComparer.Ordinal);
        using var scanner = new HexKeyCandidateScanner((candidate, salt) =>
        {
            foreach (var target in targets)
            {
                if (validated.ContainsKey(target.DatabaseGroupFingerprint))
                {
                    continue;
                }

                if (!salt.IsEmpty
                    && !target.FirstPage.AsSpan(0, WeixinWindows4SqlCipherKeyValidator.SaltSize).SequenceEqual(salt))
                {
                    continue;
                }

                var validation = validator.ValidateFirstPage(target.FirstPage, candidate);
                if (!validation.IsValid || string.IsNullOrWhiteSpace(validation.EncryptionProfileId))
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
                    target.ShardNumber,
                    validation.EncryptionProfileId));
            }

            return validated.Count != targets.Count;
        }, WcdbMemoryProtectionMask, budget.MaximumCandidates);

        var started = Stopwatch.StartNew();
        long scannedBytes = 0;
        var reachedLimit = false;
        foreach (var process in processes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remainingDuration = budget.MaximumDuration - started.Elapsed;
            var remainingBytes = budget.MaximumScanBytes - scannedBytes;
            if (remainingDuration <= TimeSpan.Zero || remainingBytes <= 0)
            {
                reachedLimit = true;
                break;
            }

            try
            {
                using var source = memorySourceFactory.Open(process);
                var scan = source.Scan(
                    scanner.ProcessChunk,
                    new KeyAcquisitionBudget(remainingDuration, remainingBytes, budget.MaximumCandidates),
                    cancellationToken);
                scannedBytes = checked(scannedBytes + scan.ScannedBytes);
                reachedLimit |= scan.ReachedLimit;
                scanProgress?.Invoke(new WeixinKeyScanProgress(
                    new ProcessMemoryScanResult(
                        scan.RegionCount,
                        scannedBytes,
                        reachedLimit,
                        scanner.CandidateCount),
                    validated.Count,
                    targets.Count,
                    targets
                        .Select(static (target, index) => (target, ordinal: index + 1))
                        .Where(item => !validated.ContainsKey(item.target.DatabaseGroupFingerprint))
                        .Select(static item => (int?)item.ordinal)
                        .FirstOrDefault()));
            }
            catch (UnauthorizedAccessException) when (validated.Count < targets.Count)
            {
                // A same-image child can exit between identity verification and
                // opening its read-only handle. Continue within the fixed tree.
            }

            if (validated.Count == targets.Count)
            {
                break;
            }
        }

        var missingTargets = targets
            .Where(target => !validated.ContainsKey(target.DatabaseGroupFingerprint))
            .ToArray();
        if (missingTargets.Any(target => !WeixinWindows41155DatabasePolicy.CanIntentionallyIgnore(target.SourceRelativePath)))
        {
            foreach (var item in validated.Values)
            {
                item.KeyMaterial.Dispose();
            }

            throw new AppFailureException(ErrorCode.KeyCandidateNotFound, $"The 4.1.11.55 Profile validated {validated.Count} of {targets.Count} database groups; materialization is refused.");
        }

        return validated.Values.ToArray();
    }
}

internal interface IWeixinModuleIdentityVerifier
{
    Task VerifyAsync(IReadOnlyList<VerifiedWeixinProcess> processes, CancellationToken cancellationToken);
}

internal sealed class VersionedWcdbModuleIdentityVerifier : IWeixinModuleIdentityVerifier
{
    public async Task VerifyAsync(IReadOnlyList<VerifiedWeixinProcess> processes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processes);
        var imageDirectories = processes
            .Select(static process => Path.GetDirectoryName(process.ImagePath))
            .Where(static directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (imageDirectories.Length != 1)
        {
            throw new AppFailureException(ErrorCode.ProcessIdentityMismatch, "The verified Weixin process tree did not resolve to one installation directory.");
        }

        var versions = processes.Select(static process => process.ProductVersion).Distinct(StringComparer.Ordinal).ToArray();
        if (versions.Length != 1 || !string.Equals(versions[0], WeixinWindows41155Profile.SupportedVersion, StringComparison.Ordinal))
        {
            throw new AppFailureException(ErrorCode.UnsupportedWeixinVersion, "The verified Weixin process tree did not resolve to the Profile's exact version directory.");
        }

        // Current Weixin keeps the stable launcher at the installation root
        // and versioned WCDB code beneath <version>/Weixin.dll. The Profile
        // binds that exact path; it never searches for another DLL.
        var installationRoot = Path.GetFullPath(imageDirectories[0]!);
        var modulePath = Path.GetFullPath(Path.Combine(installationRoot, versions[0], "Weixin.dll"));
        var requiredPrefix = installationRoot.EndsWith(Path.DirectorySeparatorChar)
            ? installationRoot
            : installationRoot + Path.DirectorySeparatorChar;
        if (!modulePath.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppFailureException(ErrorCode.ProcessIdentityMismatch, "The exact WCDB module path escaped the verified Weixin installation directory.");
        }

        if (!File.Exists(modulePath) || (File.GetAttributes(modulePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new AppFailureException(ErrorCode.ProcessIdentityMismatch, "The exact WCDB module required by the selected Profile was not found as a regular versioned file.");
        }

        await using var stream = new FileStream(
            modulePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        if (!string.Equals(hash, WeixinWindows41155Profile.SupportedWcdbModuleSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppFailureException(ErrorCode.ProcessIdentityMismatch, "The adjacent WCDB module hash does not match the selected exact Profile.");
        }
    }
}

internal sealed record WeixinKeyScanProgress(
    ProcessMemoryScanResult Memory,
    int ValidatedGroups,
    int TotalGroups,
    int? FirstUnvalidatedGroupOrdinal);

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
        var wal = files.FirstOrDefault(item => item.RelativePath.Replace('\\', '/').Equals(basePath + "-wal", StringComparison.OrdinalIgnoreCase));
        var shm = files.FirstOrDefault(item => item.RelativePath.Replace('\\', '/').Equals(basePath + "-shm", StringComparison.OrdinalIgnoreCase));
        return WeChatVoice.Core.Models.DatabaseGroupFingerprint.Compute(
            basePath,
            logicalRole,
            shardNumber,
            main.ByteLength,
            main.Sha256,
            wal?.ByteLength,
            wal?.Sha256,
            shm?.ByteLength,
            shm?.Sha256);
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
