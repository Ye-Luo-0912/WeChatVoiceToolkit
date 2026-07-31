using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Serialization;
using System.Diagnostics;

namespace WeChatVoice.Infrastructure.Snapshots;

/// <summary>
/// Creates a group-level file snapshot. Every attempt inventories the complete
/// source set before and after copying, so a database, WAL, and SHM group is
/// accepted only when the whole set is stable at the attempt boundary.
/// </summary>
public sealed class SnapshotCreator : ISnapshotCreator
{
    private const string RootManifestName = "snapshot.json";
    private const string InternalMetadataDirectory = ".wechatvoice";
    private const string InternalManifestName = "snapshot-manifest.json";

    private readonly SnapshotFileCopier _fileCopier;
    private readonly ISnapshotSourceActivityProbe _sourceActivityProbe;

    public SnapshotCreator()
        : this(new ProcessSnapshotSourceActivityProbe())
    {
    }

    public SnapshotCreator(ISnapshotSourceActivityProbe sourceActivityProbe)
        : this(sourceActivityProbe, new SnapshotFileCopier())
    {
    }

    internal SnapshotCreator(ISnapshotSourceActivityProbe sourceActivityProbe, SnapshotFileCopier fileCopier)
    {
        _sourceActivityProbe = sourceActivityProbe ?? throw new ArgumentNullException(nameof(sourceActivityProbe));
        _fileCopier = fileCopier ?? throw new ArgumentNullException(nameof(fileCopier));
    }

    public async Task<SnapshotManifest> CreateAsync(SnapshotRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var sourceDirectory = Path.GetFullPath(request.SourceDirectory);
        var outputDirectory = Path.GetFullPath(request.OutputDirectory);

        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"Snapshot source directory was not found: '{sourceDirectory}'.");
        }

        EnsureDestinationIsSafe(sourceDirectory, outputDirectory);
        EnsureOutputIsAvailable(outputDirectory);

        var outputParent = Path.GetDirectoryName(outputDirectory)
            ?? throw new ArgumentException("The snapshot output path must include a parent directory.", nameof(request));
        Directory.CreateDirectory(outputParent);

        var outputDirectoryAlreadyExists = Directory.Exists(outputDirectory);
        var processNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SnapshotSourceChangedException? lastSourceChange = null;

        for (var attempt = 1; attempt <= request.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var activityBefore = _sourceActivityProbe.Probe();
            processNames.UnionWith(activityBefore.ProcessNames);
            if (activityBefore.IsLive && !request.AllowLiveSource)
            {
                throw new LiveSnapshotSourceException(activityBefore.ProcessNames);
            }

            var stagingDirectory = Path.Combine(
                outputParent,
                $".{Path.GetFileName(outputDirectory)}.snapshot-{Guid.NewGuid():N}.staging");
            var completed = false;

            try
            {
                var beforeInventory = CaptureInventory(sourceDirectory);
                Directory.CreateDirectory(stagingDirectory);
                var files = new List<SnapshotFileRecord>(beforeInventory.Files.Count);

                foreach (var pair in beforeInventory.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sourcePath = CombineUnderRoot(sourceDirectory, pair.Key);
                    var destinationPath = CombineUnderRoot(stagingDirectory, pair.Key);
                    var copied = await _fileCopier.CopyStableFileAsync(
                        sourcePath,
                        destinationPath,
                        requireStableSource: request.RequireStableSource,
                        cancellationToken).ConfigureAwait(false);

                    files.Add(new SnapshotFileRecord(
                        pair.Key,
                        copied.Length,
                        copied.Sha256,
                        new DateTimeOffset(copied.LastWriteTimeUtc, TimeSpan.Zero),
                        copied.FileId));
                }

                var afterInventory = CaptureInventory(sourceDirectory);
                var activityAfter = _sourceActivityProbe.Probe();
                processNames.UnionWith(activityAfter.ProcessNames);
                if (activityAfter.IsLive && !request.AllowLiveSource)
                {
                    throw new LiveSnapshotSourceException(activityAfter.ProcessNames);
                }

                if (!beforeInventory.IsEquivalentTo(afterInventory))
                {
                    throw new SnapshotSourceChangedException(
                        $"The snapshot source file set or metadata changed during attempt {attempt}.");
                }

                var manifest = new SnapshotManifest(
                    sourceDirectory,
                    outputDirectory,
                    DateTimeOffset.UtcNow,
                    files,
                    PotentiallyInconsistent: request.AllowLiveSource,
                    AttemptCount: attempt,
                    SourceProcessNames: processNames);

                await AtomicFileWriter.WriteJsonAsync(
                    Path.Combine(stagingDirectory, InternalMetadataDirectory, InternalManifestName),
                    manifest,
                    InfrastructureJson.Indented,
                    cancellationToken).ConfigureAwait(false);

                if (outputDirectoryAlreadyExists)
                {
                    // The directory was verified empty before attempts began. If it
                    // changed concurrently, the move/delete fails without touching
                    // the caller's existing data.
                    Directory.Delete(outputDirectory);
                }

                Directory.Move(stagingDirectory, outputDirectory);
                completed = true;
                return manifest;
            }
            catch (SnapshotSourceChangedException exception) when (attempt < request.MaxAttempts)
            {
                lastSourceChange = exception;
            }
            finally
            {
                if (!completed)
                {
                    TryDeleteDirectory(stagingDirectory);
                }
            }
        }

        throw lastSourceChange
            ?? new SnapshotSourceChangedException($"The snapshot source changed after {request.MaxAttempts} attempts.");
    }

    private static SnapshotSourceInventory CaptureInventory(string sourceDirectory)
    {
        try
        {
            return SnapshotFileCopier.CaptureInventory(sourceDirectory, IncludeSourceFile);
        }
        catch (FileNotFoundException exception)
        {
            throw new SnapshotSourceChangedException(
                $"The snapshot source changed while it was being inventoried: '{exception.FileName ?? exception.Message}'.");
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new SnapshotSourceChangedException(
                $"The snapshot source directory changed while it was being inventoried: '{exception.Message}'.");
        }
    }

    private static bool IncludeSourceFile(string relativePath)
    {
        if (string.Equals(relativePath, RootManifestName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !string.Equals(relativePath, InternalMetadataDirectory, StringComparison.OrdinalIgnoreCase)
            && !relativePath.StartsWith(InternalMetadataDirectory + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureOutputIsAvailable(string outputDirectory)
    {
        if (File.Exists(outputDirectory))
        {
            throw new IOException($"Snapshot output path is an existing file: '{outputDirectory}'.");
        }

        if (!Directory.Exists(outputDirectory))
        {
            return;
        }

        var attributes = File.GetAttributes(outputDirectory);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("A snapshot output directory cannot be a reparse point.");
        }

        if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            throw new IOException($"Snapshot output directory is not empty: '{outputDirectory}'. Choose a new or empty destination.");
        }
    }

    private static void EnsureDestinationIsSafe(string sourceDirectory, string outputDirectory)
    {
        if (IsSameOrChildPath(outputDirectory, sourceDirectory))
        {
            throw new ArgumentException(
                "The snapshot output directory must not be the source directory or be located inside it.",
                nameof(outputDirectory));
        }

        if (IsSameOrChildPath(sourceDirectory, outputDirectory))
        {
            throw new ArgumentException(
                "The snapshot source directory must not be located inside the output directory.",
                nameof(outputDirectory));
        }
    }

    private static string CombineUnderRoot(string rootDirectory, string relativePath)
    {
        var platformRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(rootDirectory, platformRelativePath));
        if (!IsSameOrChildPath(candidate, rootDirectory) || string.Equals(candidate, rootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("A source path could not be represented safely inside the snapshot directory.");
        }

        return candidate;
    }

    private static bool IsSameOrChildPath(string candidatePath, string rootPath)
    {
        var candidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteDirectory(string path)
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
            // Preserve the original copy/cancellation failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the original copy/cancellation failure.
        }
    }
}

internal sealed class ProcessSnapshotSourceActivityProbe : ISnapshotSourceActivityProbe
{
    private static readonly string[] Names = ["WeChat", "WeChatAppEx", "Weixin"];

    public SnapshotSourceActivity Probe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new SnapshotSourceActivity(false);
        }

        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Names)
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    process.Dispose();
                    running.Add(name);
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }

        var processNames = running.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return new SnapshotSourceActivity(processNames.Length > 0, processNames);
    }
}

public sealed class LiveSnapshotSourceException : IOException
{
    public LiveSnapshotSourceException(IEnumerable<string>? processNames)
        : base(BuildMessage(processNames))
    {
        ProcessNames = (processNames ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<string> ProcessNames { get; }

    private static string BuildMessage(IEnumerable<string>? processNames)
    {
        var names = (processNames ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return names.Length == 0
            ? "A recognized WeChat process is running. Exit WeChat completely or pass --allow-live-source explicitly."
            : $"WeChat process(es) are still running: {string.Join(", ", names)}. Exit WeChat completely or pass --allow-live-source explicitly.";
    }
}
