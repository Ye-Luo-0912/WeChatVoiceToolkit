using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WeChatVoice.Core.Errors;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Serialization;
using WeChatVoice.Infrastructure.Snapshots;
using WeChatVoice.Infrastructure.Sqlite;

namespace WeChatVoice.Infrastructure.Materialization;

/// <summary>
/// Runs a fixed external decryptor protocol and produces a verified ordinary
/// SQLite workspace. The executable is configured by the host; arguments,
/// source verification, output mapping, and acceptance rules remain fixed.
/// </summary>
public sealed class ExternalDatabaseMaterializer : IDatabaseMaterializer
{
    private const int OutputLimit = 64 * 1024;
    private readonly string _executablePath;
    private readonly string _backendVersion;
    private readonly string _expectedBinarySha256;

    public ExternalDatabaseMaterializer(string executablePath, string backendVersion = "1", string? expectedBinarySha256 = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(backendVersion);
        _executablePath = Path.GetFullPath(executablePath);
        _backendVersion = backendVersion;
        _expectedBinarySha256 = string.IsNullOrWhiteSpace(expectedBinarySha256)
            ? "untrusted-development-backend"
            : expectedBinarySha256.Trim().ToLowerInvariant();
    }

    public string Id => "external-decryptor-v1";

    public string Version => _backendVersion;

    public string ExpectedBinarySha256 => _expectedBinarySha256;

    public async Task<VerifiedMaterialization> MaterializeAsync(
        VerifiedRawSnapshot snapshot,
        MaterializationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_executablePath))
        {
            throw new AppFailureException(ErrorCode.WorkerBundleUntrusted, "The configured external decryptor was not found.");
        }

        var rawSnapshot = snapshot.Snapshot;
        var sourceRoot = Path.GetFullPath(rawSnapshot.SnapshotDirectory);
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"The raw snapshot directory was not found: '{sourceRoot}'.");
        }

        var sourceProbe = await new DataSetProbeService().ProbeAsync(
            snapshot,
            new DataSetProbeOptions(IncludeLocalPaths: true),
            cancellationToken).ConfigureAwait(false);
        if (sourceProbe.DataSet.Databases.Count == 0)
        {
            throw new DatabaseMaterializationException(null, null, null, "The raw snapshot contains no database artifacts to materialize.");
        }

        var missingRequiredRoles = new[] { "message", "media", "contact" }
            .Where(role => !sourceProbe.DataSet.Databases.Any(database => string.Equals(database.LogicalRole, role, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (missingRequiredRoles.Length > 0)
        {
            throw new DatabaseMaterializationException(null, null, null, $"The raw snapshot is missing required database roles: {string.Join(", ", missingRequiredRoles)}.");
        }

        var incompleteGroups = sourceProbe.Issues
            .Where(issue => issue.Code is "missing-media-database" or "missing-message-database" or "incomplete-wal-pair")
            .ToArray();
        if (incompleteGroups.Length > 0)
        {
            throw new DatabaseMaterializationException(null, null, null, "The raw snapshot is missing a required message/media database role or has an incomplete WAL group and cannot be materialized for voice export.");
        }

        var backendSha256 = await FileHashing.ComputeSha256Async(_executablePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(_expectedBinarySha256, "untrusted-development-backend", StringComparison.Ordinal)
            && !string.Equals(_expectedBinarySha256, backendSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new DatabaseMaterializationException(null, null, null, "The materialization backend binary hash does not match its registered expectation.");
        }

        if (Directory.Exists(options.OutputDirectory) || File.Exists(options.OutputDirectory))
        {
            throw new IOException("The materialization output target must not already exist.");
        }

        var parent = Path.GetDirectoryName(options.OutputDirectory)
            ?? throw new ArgumentException("The materialization output directory must have a parent.", nameof(options));
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileName(options.OutputDirectory)}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(staging);
        var operationId = Guid.NewGuid().ToString("N");
        await MaterializationStateStore.TransitionAsync(
            staging,
            Array.Empty<string>(),
            MaterializationCommitStates.Staging,
            operationId,
            failureCode: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Exception? primaryFailure = null;
        try
        {
            DecryptorResult result;
            using (var backendTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                backendTimeout.CancelAfter(options.BackendTimeout);
                try
                {
                    result = await RunDecryptorAsync(sourceRoot, staging, backendTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && backendTimeout.IsCancellationRequested)
                {
                    throw new DatabaseMaterializationException(null, null, null, $"The materialization backend exceeded its {options.BackendTimeout} time limit.");
                }
            }
            if (result.ExitCode != 0)
            {
                throw new DatabaseMaterializationException(result.ExitCode, result.StandardOutput, result.StandardError, "The external decryptor returned a non-zero exit code.");
            }

            var validation = await ValidateOutputAsync(staging, sourceProbe.DataSet.Databases, cancellationToken).ConfigureAwait(false);
            if (validation.Databases.Any(static database => database.Status == MaterializationDatabaseStatus.Failed))
            {
                throw new DatabaseMaterializationException(result.ExitCode, result.StandardOutput, result.StandardError, "The external decryptor did not materialize every required source database.");
            }

            var workspaceId = ComputeWorkspaceId(rawSnapshot.SnapshotId, backendSha256, _backendVersion, validation.Databases);
            var manifestPath = Path.Combine(staging, ".wechatvoice", "materialization-manifest.json");
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            var manifest = new MaterializationManifest(
                workspaceId,
                rawSnapshot.SnapshotId,
                Id,
                _backendVersion,
                backendSha256,
                validation.Databases,
                validation.Files,
                AccountId: SnapshotSourceIdentity.TryDerive(rawSnapshot.Manifest.SourceDirectory, rawSnapshot.Manifest.Files)?.AccountCandidate);
            await using (var manifestStream = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(manifestStream, manifest, InfrastructureJson.Indented, cancellationToken).ConfigureAwait(false);
                await manifestStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await MaterializationStateStore.TransitionAsync(
                staging,
                [MaterializationCommitStates.Staging],
                MaterializationCommitStates.DatabasesCommitted,
                operationId,
                failureCode: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            Directory.Move(staging, options.OutputDirectory);
            var movedManifestPath = Path.Combine(options.OutputDirectory, ".wechatvoice", "materialization-manifest.json");
            return new VerifiedMaterialization(new MaterializationResult(
                workspaceId,
                rawSnapshot.SnapshotId,
                Id,
                _backendVersion,
                backendSha256,
                options.OutputDirectory,
                validation.Databases,
                validation.Files,
                movedManifestPath), DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            if (Directory.Exists(options.OutputDirectory))
            {
                try
                {
                    await MaterializationStateStore.TryTransitionToFailedRecoverableAsync(
                        options.OutputDirectory,
                        operationId,
                        ErrorCode.MaterializationInvalid.ToString(),
                        CancellationToken.None).ConfigureAwait(false);
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
                        throw new DatabaseMaterializationException(null, null, null, $"Materialization staging cleanup failed: {cleanupException.Message}");
                    }

                    throw new AggregateException(primaryFailure, cleanupException);
                }
            }
        }
    }

    private async Task<DecryptorResult> RunDecryptorAsync(string inputRoot, string outputRoot, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--input-root");
        startInfo.ArgumentList.Add(inputRoot);
        startInfo.ArgumentList.Add("--output-root");
        startInfo.ArgumentList.Add(outputRoot);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new DatabaseMaterializationException(null, null, null, "The external decryptor could not be started.");
        }

        var stdoutTask = ReadBoundedAsync(process.StandardOutput, cancellationToken);
        var stderrTask = ReadBoundedAsync(process.StandardError, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            return new DecryptorResult(process.ExitCode, stdoutTask.Result, stderrTask.Result);
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static async Task<OutputValidation> ValidateOutputAsync(
        string outputRoot,
        IReadOnlyList<DatabaseArtifact> sourceDatabases,
        CancellationToken cancellationToken)
    {
        var outputFiles = EnumerateRegularFilesStrict(outputRoot)
            .Where(path => !MaterializationStateStore.IsStatePath(Path.GetRelativePath(outputRoot, path))
                && !MaterializationStateStore.IsLockPath(Path.GetRelativePath(outputRoot, path)))
            .ToArray();
        var files = new List<MaterializationFile>();
        foreach (var path in outputFiles.Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = SnapshotFileCopier.NormalizeRelativePath(Path.GetRelativePath(outputRoot, path));
            var info = new FileInfo(path);
            files.Add(new MaterializationFile(relative, await FileHashing.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false), info.Length));
        }

        var outputManifestPath = CombineUnderRoot(outputRoot, ".wechatvoice/materialization-output.json");
        if (!File.Exists(outputManifestPath))
        {
            throw new DatabaseMaterializationException(null, null, null, "The materialization backend did not produce the required explicit output manifest.");
        }

        MaterializationOutputManifest outputManifest;
        await using (var manifestStream = new FileStream(outputManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            outputManifest = await JsonSerializer.DeserializeAsync<MaterializationOutputManifest>(manifestStream, InfrastructureJson.Compact, cancellationToken).ConfigureAwait(false)
                ?? throw new DatabaseMaterializationException(null, null, null, "The materialization output manifest was empty.");
        }

        if (outputManifest.FormatVersion != MaterializationOutputManifest.CurrentFormatVersion)
        {
            throw new DatabaseMaterializationException(null, null, null, $"Unsupported materialization output manifest format: {outputManifest.FormatVersion}.");
        }

        var sourceByPath = sourceDatabases.ToDictionary(item => NormalizeRelative(item.DatabasePath), StringComparer.OrdinalIgnoreCase);
        var mappedOutputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var databases = new List<MaterializedDatabase>();
        foreach (var mapping in outputManifest.Databases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = NormalizeRelative(mapping.SourceRelativePath);
            if (!sourceByPath.TryGetValue(sourcePath, out var source))
            {
                throw new DatabaseMaterializationException(null, null, null, $"The materialization output manifest references an unknown source database: '{mapping.SourceRelativePath}'.");
            }

            if (!usedSources.Add(sourcePath))
            {
                throw new DatabaseMaterializationException(null, null, null, $"The materialization output manifest maps a source database more than once: '{mapping.SourceRelativePath}'.");
            }

            var outputRelative = string.IsNullOrWhiteSpace(mapping.OutputRelativePath) ? string.Empty : NormalizeRelative(mapping.OutputRelativePath);
            if (mapping.Status is MaterializationDatabaseStatus.IntentionallyIgnored or MaterializationDatabaseStatus.Failed)
            {
                databases.Add(new MaterializedDatabase(
                    source.DatabasePath,
                    source.DatabaseGroupFingerprint ?? string.Empty,
                    outputRelative,
                    source.LogicalRole,
                    source.ShardNumber,
                    string.Empty,
                    0,
                    string.Empty,
                    mapping.Status,
                    mapping.Error ?? "The backend did not materialize this source database."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(outputRelative))
            {
                throw new DatabaseMaterializationException(null, null, null, $"The materialization output manifest has no output path for '{mapping.SourceRelativePath}'.");
            }

            var outputPath = CombineUnderRoot(outputRoot, outputRelative);
            if (!File.Exists(outputPath))
            {
                throw new DatabaseMaterializationException(null, null, null, $"The mapped materialization output is missing: '{outputRelative}'.");
            }

            await ValidateSqliteAsync(outputPath, cancellationToken).ConfigureAwait(false);
            mappedOutputPaths.Add(outputRelative);
            var info = new FileInfo(outputPath);
            var hash = await FileHashing.ComputeSha256Async(outputPath, cancellationToken).ConfigureAwait(false);
            var schema = await new SqliteSchemaInspector().InspectAsync(outputPath, new SchemaInspectionOptions(IncludeLocalPaths: false, PrecomputedSha256: hash, PrecomputedByteLength: info.Length), cancellationToken).ConfigureAwait(false);
            databases.Add(new MaterializedDatabase(
                source.DatabasePath,
                source.DatabaseGroupFingerprint ?? string.Empty,
                outputRelative,
                source.LogicalRole,
                source.ShardNumber,
                hash,
                info.Length,
                schema.SchemaFingerprint ?? string.Empty,
                mapping.Status,
                mapping.Error));
        }

        foreach (var source in sourceDatabases.Where(item => !usedSources.Contains(NormalizeRelative(item.DatabasePath))))
        {
            databases.Add(new MaterializedDatabase(
                source.DatabasePath,
                source.DatabaseGroupFingerprint ?? string.Empty,
                string.Empty,
                source.LogicalRole,
                source.ShardNumber,
                string.Empty,
                0,
                string.Empty,
                MaterializationDatabaseStatus.Failed,
                "The source database is absent from the backend output manifest."));
        }

        var actualOutputDatabases = outputFiles
            .Where(static path => string.Equals(Path.GetExtension(path), ".db", StringComparison.OrdinalIgnoreCase))
            .Select(path => NormalizeRelative(Path.GetRelativePath(outputRoot, path)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actualOutputDatabases.SetEquals(mappedOutputPaths))
        {
            throw new DatabaseMaterializationException(null, null, null, "The materialization output contains database files that are not covered by its explicit output manifest.");
        }

        var requiredRoles = sourceDatabases.Where(static item => item.LogicalRole is "message" or "media" or "contact")
            .Select(static item => (item.LogicalRole, item.ShardNumber))
            .ToHashSet();
        var materializedRoles = databases.Where(static item => item.Status is MaterializationDatabaseStatus.Materialized or MaterializationDatabaseStatus.CopiedAsPlaintext)
            .Select(static item => (item.LogicalRole, item.ShardNumber))
            .ToHashSet();
        if (requiredRoles.Except(materializedRoles).Any())
        {
            throw new DatabaseMaterializationException(null, null, null, "The materializer output is missing one or more required message/media/contact databases.");
        }

        return new OutputValidation(databases.OrderBy(static item => item.SourceRelativePath, StringComparer.OrdinalIgnoreCase).ToArray(), files);
    }

    private static async Task ValidateSqliteAsync(string path, CancellationToken cancellationToken)
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
            throw new DatabaseMaterializationException(null, null, null, $"Materializer output is not a plain SQLite database: '{Path.GetFileName(path)}'.");
        }

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
        var quickCheck = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(quickCheck, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new DatabaseMaterializationException(null, null, null, $"SQLite quick_check failed for '{Path.GetFileName(path)}': {quickCheck ?? "unknown"}.");
        }
    }

    private static IEnumerable<string> EnumerateRegularFilesStrict(string root)
    {
        var attributes = File.GetAttributes(root);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new DatabaseMaterializationException(null, null, null, "Materializer output root cannot be a reparse point.");
        }

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                var entryAttributes = File.GetAttributes(entry);
                if ((entryAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new DatabaseMaterializationException(null, null, null, $"Materializer output contains a reparse point: '{entry}'.");
                }

                if ((entryAttributes & FileAttributes.Directory) != 0)
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

    private static string ComputeWorkspaceId(string snapshotId, string backendSha256, string backendVersion, IEnumerable<MaterializedDatabase> databases)
    {
        var canonical = string.Join('\n', databases.OrderBy(static item => item.OutputRelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(item => string.Join('|', item.OutputRelativePath, item.Sha256, item.ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture), item.SchemaFingerprint)));
        var bytes = Encoding.UTF8.GetBytes(string.Join('|', snapshotId, backendSha256, backendVersion, canonical));
        return "materialized-" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string NormalizeRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            throw new DatabaseMaterializationException(null, null, null, "The materialization output manifest contains an invalid relative path.");
        }

        var normalized = SnapshotFileCopier.NormalizeRelativePath(path);
        if (normalized.Equals(".", StringComparison.Ordinal) || normalized.StartsWith("../", StringComparison.Ordinal) || normalized.Contains("/../", StringComparison.Ordinal))
        {
            throw new DatabaseMaterializationException(null, null, null, "The materialization output manifest contains a path outside its root.");
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
            throw new DatabaseMaterializationException(null, null, null, "The materialization output manifest contains a path outside its root.");
        }

        return candidate;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (builder.Length < OutputLimit)
            {
                builder.Append(buffer, 0, Math.Min(read, OutputLimit - builder.Length));
            }
        }

        return builder.Length == OutputLimit ? builder.ToString() + "…" : builder.ToString();
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

    private sealed record OutputValidation(IReadOnlyList<MaterializedDatabase> Databases, IReadOnlyList<MaterializationFile> Files);

    private sealed record DecryptorResult(int ExitCode, string StandardOutput, string StandardError);
}

public sealed class DatabaseMaterializationException : IOException
{
    public DatabaseMaterializationException(int? ExitCode, string? StandardOutput, string? StandardError, string message)
        : base(message)
    {
        this.ExitCode = ExitCode;
        this.StandardOutput = SensitiveOutputRedactor.Redact(StandardOutput);
        this.StandardError = SensitiveOutputRedactor.Redact(StandardError);
    }

    public int? ExitCode { get; }

    public string? StandardOutput { get; }

    public string? StandardError { get; }
}
