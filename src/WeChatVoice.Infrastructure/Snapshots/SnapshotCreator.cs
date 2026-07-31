using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Serialization;

namespace WeChatVoice.Infrastructure.Snapshots;

/// <summary>
/// Creates a file-level snapshot without making database- or product-specific
/// assumptions. SQLite database, WAL, and SHM files are copied in the same way
/// as every other regular file in the selected source tree.
/// </summary>
public sealed class SnapshotCreator : ISnapshotCreator
{
    private readonly SnapshotFileCopier _fileCopier;

    public SnapshotCreator()
        : this(new SnapshotFileCopier())
    {
    }

    internal SnapshotCreator(SnapshotFileCopier fileCopier)
    {
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

        if (File.Exists(outputDirectory))
        {
            throw new IOException($"Snapshot output path is an existing file: '{outputDirectory}'.");
        }

        var outputDirectoryAlreadyExists = Directory.Exists(outputDirectory);
        if (outputDirectoryAlreadyExists)
        {
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

        var outputParent = Path.GetDirectoryName(outputDirectory)
            ?? throw new ArgumentException("The snapshot output path must include a parent directory.", nameof(request));
        Directory.CreateDirectory(outputParent);

        var stagingDirectory = Path.Combine(
            outputParent,
            $".{Path.GetFileName(outputDirectory)}.snapshot-{Guid.NewGuid():N}.staging");
        var completed = false;

        try
        {
            Directory.CreateDirectory(stagingDirectory);
            var files = new List<SnapshotFileRecord>();

            foreach (var sourcePath in SnapshotFileCopier.EnumerateRegularFiles(sourceDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
                var destinationPath = CombineUnderRoot(stagingDirectory, relativePath);
                var copied = await _fileCopier.CopyStableFileAsync(
                    sourcePath,
                    destinationPath,
                    request.RequireStableSource,
                    cancellationToken).ConfigureAwait(false);

                files.Add(new SnapshotFileRecord(
                    NormalizeManifestPath(relativePath),
                    copied.ByteLength,
                    copied.Sha256,
                    new DateTimeOffset(copied.LastWriteTimeUtc, TimeSpan.Zero)));
            }

            var manifest = new SnapshotManifest(
                sourceDirectory,
                outputDirectory,
                DateTimeOffset.UtcNow,
                files);

            await AtomicFileWriter.WriteJsonAsync(
                Path.Combine(stagingDirectory, "snapshot.json"),
                manifest,
                InfrastructureJson.Indented,
                cancellationToken).ConfigureAwait(false);

            if (outputDirectoryAlreadyExists)
            {
                // The directory was verified empty before staging began. If it
                // changed concurrently, Delete fails and the caller's data stays
                // untouched while the staging directory is cleaned in finally.
                Directory.Delete(outputDirectory);
            }

            Directory.Move(stagingDirectory, outputDirectory);
            completed = true;
            return manifest;
        }
        finally
        {
            if (!completed)
            {
                TryDeleteDirectory(stagingDirectory);
            }
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
        var candidate = Path.GetFullPath(Path.Combine(rootDirectory, relativePath));
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

    private static string NormalizeManifestPath(string relativePath)
        => relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

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
            // Preserve the original copy/cancellation failure. The unique staging
            // directory makes any remaining partial data easy to identify safely.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the original copy/cancellation failure.
        }
    }
}
