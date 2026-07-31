using System.Security.Cryptography;
using System.Text;
using WeChatVoice.Core.Models;

namespace WeChatVoice.Infrastructure.Sqlite;

/// <summary>
/// Creates an executable local workspace from a decrypted database root. The
/// resulting JSON contains local paths and is intentionally distinct from the
/// shareable DataSetProbe report.
/// </summary>
public sealed class LocalWorkspaceCreator
{
    private readonly DataSetProbeService _probeService;

    public LocalWorkspaceCreator(DataSetProbeService? probeService = null)
        => _probeService = probeService ?? new DataSetProbeService();

    public async Task<LocalWorkspace> CreateAsync(string rootDirectory, CancellationToken cancellationToken)
    {
        var probe = await _probeService.ProbeAsync(
            rootDirectory,
            new DataSetProbeOptions(IncludeLocalPaths: true),
            cancellationToken).ConfigureAwait(false);
        var sourceRoot = probe.SourceRoot
            ?? throw new InvalidDataException("The local workspace probe did not retain its source root.");
        var workspaceId = ComputeWorkspaceId(sourceRoot, probe.DataSet.DataSetId);
        return new LocalWorkspace(
            workspaceId,
            sourceRoot,
            probe.DataSet,
            DateTimeOffset.UtcNow,
            probe.Issues,
            probe.AdapterCandidates);
    }

    private static string ComputeWorkspaceId(string sourceRoot, string dataSetId)
    {
        var canonical = Encoding.UTF8.GetBytes(Path.GetFullPath(sourceRoot) + "|" + dataSetId);
        return "workspace-" + Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant()[..16];
    }
}
