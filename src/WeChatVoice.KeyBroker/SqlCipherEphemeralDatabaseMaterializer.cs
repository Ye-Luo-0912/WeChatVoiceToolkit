using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Materialization;
using WeChatVoice.Infrastructure.Serialization;
using WeChatVoice.Infrastructure.Snapshots;
using WeChatVoice.Infrastructure.Sqlite;
using WeChatVoice.KeyAcquisition.Models;
using WeChatVoice.KeyAcquisition.Ports;
using WeChatVoice.KeyAcquisition.Validation;
using WeChatVoice.Windows;

namespace WeChatVoice.KeyBroker;

/// <summary>
/// Runs the bundled SQLCipher compatibility worker once per verified database
/// group. The key crosses only the private child-process stdin pipe and is
/// cleared from the envelope as soon as it has been written.
/// </summary>
internal sealed class SqlCipherEphemeralDatabaseMaterializer : IEphemeralDatabaseMaterializer
{
    private const int WorkerKeySize = 32;
    private const int ProtocolHeaderSize = 5;
    private const int OutputLimit = 64 * 1024;
    private static readonly IReadOnlySet<string> EncryptionProfiles = new HashSet<string>(StringComparer.Ordinal)
    {
        WeixinWindows4SqlCipher3Page4096KeyValidator.EncryptionProfileId,
        WeixinWindows4SqlCipherKeyValidator.EncryptionProfileId,
    };
    private readonly string workerPath;
    private readonly bool allowDevelopmentWorker;
    private readonly string? finalWorkspaceUserSid;
    private readonly Action<int, int>? progress;
    private readonly Action<string>? checkpoint;

    internal SqlCipherEphemeralDatabaseMaterializer(
        string? workerPath = null,
        bool allowDevelopmentWorker = false,
        Action<int, int>? progress = null,
        Action<string>? checkpoint = null,
        string? finalWorkspaceUserSid = null)
    {
        this.workerPath = Path.GetFullPath(workerPath ?? Path.Combine(AppContext.BaseDirectory, "WeChatVoice.SqlCipherWorker.exe"));
        this.allowDevelopmentWorker = allowDevelopmentWorker || (workerPath is not null && Path.GetExtension(workerPath).Equals(".dll", StringComparison.OrdinalIgnoreCase));
        this.finalWorkspaceUserSid = finalWorkspaceUserSid;
        this.progress = progress;
        this.checkpoint = checkpoint;
    }

    public string BackendId => "sqlcipher-e_sqlcipher-worker";

    public IReadOnlySet<string> SupportedEncryptionProfileIds => EncryptionProfiles;

    public async Task<VerifiedMaterialization> MaterializeAsync(
        VerifiedRawSnapshot snapshot,
        VerifiedKeyAcquisition acquisition,
        MaterializationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(acquisition);
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(workerPath))
        {
            throw new AppFailureException(ErrorCode.WorkerBundleUntrusted, "The bundled SQLCipher worker was not found.");
        }

        var backendSha256 = await VerifyWorkerBundleAsync(cancellationToken).ConfigureAwait(false);
        checkpoint?.Invoke("worker-bundle-verified");

        if (Directory.Exists(options.OutputDirectory) || File.Exists(options.OutputDirectory))
        {
            throw new IOException("The materialization output target must not already exist.");
        }

        var sourceRoot = Path.GetFullPath(snapshot.Snapshot.SnapshotDirectory);
        var parent = Path.GetDirectoryName(options.OutputDirectory)
            ?? throw new ArgumentException("The materialization output directory must have a parent.", nameof(options));
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileName(options.OutputDirectory)}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(staging);
        if (BrokerDirectorySecurity.IsElevated())
        {
            BrokerDirectorySecurity.RestrictToSystemAndAdministrators(staging);
        }

        await MaterializationStateStore.WriteAsync(staging, MaterializationCommitStates.Staging, cancellationToken).ConfigureAwait(false);
        checkpoint?.Invoke("materialization-staging-created");
        Exception? primaryFailure = null;
        try
        {
            var targetByPath = snapshot.Snapshot.Manifest.Files
                .Where(static file => file.RelativePath.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(static file => NormalizeRelative(file.RelativePath), StringComparer.OrdinalIgnoreCase);
            var groupTargetByPath = (await DatabaseGroupTarget.LoadAsync(snapshot, cancellationToken).ConfigureAwait(false))
                .ToDictionary(static target => NormalizeRelative(target.SourceRelativePath), StringComparer.OrdinalIgnoreCase);
            checkpoint?.Invoke("materialization-targets-loaded");
            var materialized = new List<MaterializedDatabase>(targetByPath.Count);
            var boundPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var completed = 0;
            progress?.Invoke(completed, acquisition.Bindings.Count);
            foreach (var binding in acquisition.Bindings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalizedBindingPath = NormalizeRelative(binding.RelativeDatabasePath);
                if (!targetByPath.ContainsKey(normalizedBindingPath) || !boundPaths.Add(normalizedBindingPath))
                {
                    throw new InvalidDataException("A key binding referenced a database outside the verified Snapshot.");
                }

                var inputPath = CombineUnderRoot(sourceRoot, binding.RelativeDatabasePath);
                if (!File.Exists(inputPath))
                {
                    throw new FileNotFoundException("A bound database disappeared from the verified Snapshot.", inputPath);
                }

                // The whole DB/WAL/SHM group is opened read-only with no write
                // or delete share, and re-verified through the open handles
                // against the staged group fingerprint before the worker runs.
                if (!groupTargetByPath.TryGetValue(normalizedBindingPath, out var groupTarget))
                {
                    throw new AppFailureException(ErrorCode.DatabaseGroupUncovered, "A key binding did not resolve to a verified database group target.");
                }

                await using var sourceLease = await VerifiedDatabaseGroupLease.OpenAsync(inputPath, groupTarget, cancellationToken).ConfigureAwait(false);

                var outputRelative = NormalizeRelative(Path.Combine("databases", binding.RelativeDatabasePath));
                var outputPath = CombineUnderRoot(staging, outputRelative);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.BackendTimeout);
                try
                {
                    await RunWorkerAsync(
                        inputPath,
                        outputPath,
                        binding.EncryptionProfileId,
                        binding.ProtectedKeyMaterial,
                        timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    checkpoint?.Invoke(exception switch
                    {
                        InvalidOperationException => "worker-call-failed-state",
                        CryptographicException => "worker-call-failed-crypto",
                        IOException => "worker-call-failed-io",
                        _ => "worker-call-failed-runtime",
                    });
                    throw new IOException("The fixed SQLCipher worker call did not complete cleanly.", exception);
                }
                checkpoint?.Invoke("worker-output-produced");
                await ValidatePlaintextSqliteAsync(outputPath, timeout.Token).ConfigureAwait(false);
                checkpoint?.Invoke("worker-output-verified");
                var info = new FileInfo(outputPath);
                var hash = await FileHashing.ComputeSha256Async(outputPath, timeout.Token).ConfigureAwait(false);
                var schema = await new SqliteSchemaInspector().InspectAsync(
                    outputPath,
                    new SchemaInspectionOptions(IncludeLocalPaths: false, PrecomputedSha256: hash, PrecomputedByteLength: info.Length),
                    timeout.Token).ConfigureAwait(false);
                checkpoint?.Invoke("worker-output-inspected");
                materialized.Add(new MaterializedDatabase(
                    NormalizeRelative(binding.RelativeDatabasePath),
                    binding.DatabaseGroupFingerprint,
                    outputRelative,
                    Classify(binding.RelativeDatabasePath),
                    binding.ShardNumber,
                    hash,
                    info.Length,
                    schema.SchemaFingerprint ?? string.Empty,
                    MaterializationDatabaseStatus.Materialized,
                    null,
                    binding.EncryptionProfileId));
                completed++;
                progress?.Invoke(completed, acquisition.Bindings.Count);
            }

            foreach (var unboundPath in targetByPath.Keys.Where(path => !boundPaths.Contains(path)))
            {
                if (!WeixinWindows41155DatabasePolicy.CanIntentionallyIgnore(unboundPath)
                    || !groupTargetByPath.TryGetValue(unboundPath, out var target))
                {
                    throw new AppFailureException(ErrorCode.DatabaseGroupUncovered, "The verified key acquisition did not cover every required source database.");
                }

                materialized.Add(new MaterializedDatabase(
                    target.SourceRelativePath,
                    target.DatabaseGroupFingerprint,
                    string.Empty,
                    target.LogicalRole,
                    target.ShardNumber,
                    string.Empty,
                    0,
                    string.Empty,
                    MaterializationDatabaseStatus.IntentionallyIgnored,
                    "The exact Profile classified this migration-only auxiliary database as outside the voice-export data path, and no validated key was present."));
            }

            var files = await EnumerateFilesAsync(staging, cancellationToken).ConfigureAwait(false);
            var workspaceId = ComputeWorkspaceId(snapshot.Snapshot.SnapshotId, backendSha256, materialized);
            var manifestPath = Path.Combine(staging, ".wechatvoice", "materialization-manifest.json");
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            var manifest = new MaterializationManifest(
                workspaceId,
                snapshot.Snapshot.SnapshotId,
                BackendId,
                "e_sqlcipher-2.1.11-worker-v1",
                backendSha256,
                materialized.OrderBy(static item => item.SourceRelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
                files,
                acquisition.ProfileId,
                acquisition.ProcessVersion,
                acquisition.ProcessImageSha256,
                acquisition.WcdbModuleSha256,
                acquisition.AccountSidFingerprint,
                AccountId: SnapshotSourceIdentity.TryDerive(snapshot.Snapshot.Manifest.SourceDirectory, snapshot.Snapshot.Manifest.Files)?.AccountCandidate);
            await using (var stream = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, InfrastructureJson.Indented, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await MaterializationStateStore.WriteAsync(staging, MaterializationCommitStates.DatabasesCommitted, cancellationToken).ConfigureAwait(false);
            Directory.Move(staging, options.OutputDirectory);
            if (BrokerDirectorySecurity.IsElevated())
            {
                try
                {
                    BrokerDirectorySecurity.GrantFinalWorkspaceAccess(options.OutputDirectory, finalWorkspaceUserSid);
                }
                catch
                {
                    TryDeleteOutput(options.OutputDirectory);
                    throw;
                }
            }
            var movedManifestPath = Path.Combine(options.OutputDirectory, ".wechatvoice", "materialization-manifest.json");
            await MaterializationStateStore.WriteAsync(options.OutputDirectory, MaterializationCommitStates.DatabasesCommitted, cancellationToken).ConfigureAwait(false);
            return new VerifiedMaterialization(new MaterializationResult(
                workspaceId,
                snapshot.Snapshot.SnapshotId,
                BackendId,
                "e_sqlcipher-2.1.11-worker-v1",
                backendSha256,
                options.OutputDirectory,
                materialized,
                files,
                movedManifestPath,
                acquisition.ProfileId,
                acquisition.ProcessVersion,
                acquisition.ProcessImageSha256,
                acquisition.WcdbModuleSha256,
                acquisition.AccountSidFingerprint), DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            if (Directory.Exists(options.OutputDirectory))
            {
                try
                {
                    await MaterializationStateStore.WriteAsync(options.OutputDirectory, MaterializationCommitStates.FailedRecoverable, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception stateException) when (stateException is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                }
            }
            throw;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try
                {
                    Directory.Delete(staging, recursive: true);
                }
                catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
                {
                    if (primaryFailure is null)
                    {
                        throw new IOException("SQLCipher materialization staging cleanup failed.", cleanupException);
                    }

                    throw new AggregateException(primaryFailure, cleanupException);
                }
            }
        }
    }

    private async Task RunWorkerAsync(
        string inputPath,
        string outputPath,
        string encryptionProfileId,
        SensitiveBuffer key,
        CancellationToken cancellationToken)
    {
        if (key.Length != WorkerKeySize)
        {
            throw new InvalidDataException("The validated database key has an unsupported length.");
        }

        if (!EncryptionProfiles.Contains(encryptionProfileId))
        {
            throw new InvalidDataException("The database encryption Profile is not supported by the bundled worker.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = allowDevelopmentWorker ? "dotnet" : workerPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (allowDevelopmentWorker)
        {
            startInfo.ArgumentList.Add(workerPath);
        }
        startInfo.ArgumentList.Add("--input");
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(outputPath);
        startInfo.ArgumentList.Add("--encryption-profile");
        startInfo.ArgumentList.Add(encryptionProfileId);
        checkpoint?.Invoke("worker-starting");
        using var process = Process.Start(startInfo) ?? throw new AppFailureException(ErrorCode.WorkerFailed, "The SQLCipher worker could not be started.");
        checkpoint?.Invoke("worker-started");
        var envelope = new byte[ProtocolHeaderSize + WorkerKeySize];
        try
        {
            "WCV1"u8.CopyTo(envelope);
            envelope[4] = WorkerKeySize;
            key.CopyTo(envelope.AsSpan(ProtocolHeaderSize));
            await process.StandardInput.BaseStream.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            checkpoint?.Invoke("worker-key-sent");
            var stdoutTask = ReadBoundedAsync(process.StandardOutput, cancellationToken);
            var stderrTask = ReadBoundedAsync(process.StandardError, cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            checkpoint?.Invoke("worker-exited");
            if (process.ExitCode != 0)
            {
                throw new AppFailureException(ErrorCode.WorkerFailed, "The SQLCipher worker rejected the verified key or database.");
            }

            checkpoint?.Invoke("worker-succeeded");
        }
        catch
        {
            TryKill(process);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
            checkpoint?.Invoke("worker-envelope-cleared");
        }
    }

    private async Task<string> VerifyWorkerBundleAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await VerifyWorkerBundleCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AppFailureException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or FileNotFoundException or IOException)
        {
            throw new AppFailureException(ErrorCode.WorkerBundleUntrusted, "The SQLCipher worker bundle failed trust verification.", exception);
        }
    }

    private async Task<string> VerifyWorkerBundleCoreAsync(CancellationToken cancellationToken)
    {
        var workerHash = await FileHashing.ComputeSha256Async(workerPath, cancellationToken).ConfigureAwait(false);
        if (allowDevelopmentWorker)
        {
            return workerHash;
        }

        if (!workerPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(workerPath), Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The production SQLCipher worker must be an adjacent absolute EXE.");
        }

        if ((File.GetAttributes(workerPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The production SQLCipher worker cannot be a reparse point.");
        }

        var manifestPath = Path.Combine(Path.GetDirectoryName(workerPath)!, "WeChatVoice.SqlCipherWorker.bundle.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("The SQLCipher worker bundle manifest was not installed.", manifestPath);
        }

        if ((File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The SQLCipher worker bundle manifest cannot be a reparse point.");
        }

        await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var manifest = await JsonSerializer.DeserializeAsync<WorkerBundleManifest>(
            stream,
            InfrastructureJson.Compact,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The SQLCipher worker bundle manifest was empty.");
        if (!string.Equals(manifest.WorkerExeSha256, workerHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The SQLCipher worker EXE hash did not match its bundle manifest.");
        }

        await VerifyBundleFileAsync(manifest.DepsFile, manifest.DepsSha256, cancellationToken).ConfigureAwait(false);
        await VerifyBundleFileAsync(manifest.RuntimeConfigFile, manifest.RuntimeConfigSha256, cancellationToken).ConfigureAwait(false);
        await VerifyBundleFileAsync(manifest.NativeSqlCipherFile, manifest.NativeSqlCipherSha256, cancellationToken).ConfigureAwait(false);
        await VerifyBundleFileAsync(manifest.ProviderFile, manifest.ProviderSha256, cancellationToken).ConfigureAwait(false);
        return workerHash;
    }

    private static async Task VerifyBundleFileAsync(string? relativePath, string? expectedHash, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath) && string.IsNullOrWhiteSpace(expectedHash))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrWhiteSpace(expectedHash) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("The SQLCipher worker bundle contains an invalid file entry.");
        }

        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relativePath));
        var prefix = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            throw new InvalidDataException("The SQLCipher worker bundle references a missing file.");
        }

        var actual = await FileHashing.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A SQLCipher worker bundle file hash did not match.");
        }
    }

    private async Task ValidatePlaintextSqliteAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var header = new byte[16];
        var read = 0;
        while (read < header.Length)
        {
            var count = await stream.ReadAsync(header.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        if (read != header.Length || !header.AsSpan().SequenceEqual("SQLite format 3\0"u8))
        {
            throw new AppFailureException(ErrorCode.MaterializationInvalid, "The SQLCipher worker did not produce a plaintext SQLite database.");
        }

        checkpoint?.Invoke("plaintext-header-verified");
        WindowsSqliteProvider.EnsureInitialized();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA query_only = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText = "PRAGMA quick_check;";
        var result = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppFailureException(ErrorCode.MaterializationInvalid, "The SQLCipher worker output failed an independent SQLite quick_check.");
        }

        checkpoint?.Invoke("plaintext-quick-check-verified");
    }

    private static async Task<IReadOnlyList<MaterializationFile>> EnumerateFilesAsync(string root, CancellationToken cancellationToken)
    {
        var files = new List<MaterializationFile>();
        foreach (var path in EnumerateRegularFilesStrict(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (MaterializationStateStore.IsStatePath(Path.GetRelativePath(root, path)))
            {
                continue;
            }

            var relative = NormalizeRelative(Path.GetRelativePath(root, path));
            var info = new FileInfo(path);
            files.Add(new MaterializationFile(relative, await FileHashing.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false), info.Length));
        }

        return files.OrderBy(static item => item.OutputRelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> EnumerateRegularFilesStrict(string root)
    {
        var rootAttributes = File.GetAttributes(root);
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The materialization staging root cannot be a reparse point.");
        }

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("The materialization staging output contains a reparse point.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else
                {
                    yield return entry;
                }
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var total = 0;
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return string.Empty;
            }

            total += read;
            if (total > OutputLimit)
            {
                // Continue draining without retaining unbounded child output.
                total = OutputLimit;
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteOutput(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string ComputeWorkspaceId(string snapshotId, string backendSha256, IEnumerable<MaterializedDatabase> databases)
    {
        var canonical = string.Join('\n', databases.OrderBy(static item => item.OutputRelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(item => string.Join('|', item.OutputRelativePath, item.Sha256, item.ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture), item.SchemaFingerprint)));
        return "materialized-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('|', snapshotId, backendSha256, canonical)))).ToLowerInvariant();
    }

    private static string Classify(string path)
    {
        var lower = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (lower.StartsWith("message_", StringComparison.Ordinal))
        {
            return "message";
        }

        if (lower.StartsWith("media_", StringComparison.Ordinal))
        {
            return "media";
        }

        return lower.Contains("contact", StringComparison.Ordinal) || lower.Contains("friend", StringComparison.Ordinal)
            ? "contact"
            : "unknown";
    }

    private static string NormalizeRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            throw new InvalidDataException("A database path was not relative.");
        }

        var normalized = SnapshotFileCopier.NormalizeRelativePath(path);
        if (normalized.Equals(".", StringComparison.Ordinal) || normalized.StartsWith("../", StringComparison.Ordinal) || normalized.Contains("/../", StringComparison.Ordinal))
        {
            throw new InvalidDataException("A database path escaped its root.");
        }

        return normalized;
    }

    private static string CombineUnderRoot(string root, string relativePath)
    {
        var normalized = NormalizeRelative(relativePath);
        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A database path escaped its root.");
        }

        return candidate;
    }
}

internal sealed record WorkerBundleManifest(
    string WorkerExeSha256,
    string? DepsFile,
    string? DepsSha256,
    string? RuntimeConfigFile,
    string? RuntimeConfigSha256,
    string? NativeSqlCipherFile,
    string? NativeSqlCipherSha256,
    string? ProviderFile,
    string? ProviderSha256);
