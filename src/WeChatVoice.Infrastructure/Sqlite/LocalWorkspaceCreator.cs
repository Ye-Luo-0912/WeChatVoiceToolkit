using System.Security.Cryptography;
using System.Text;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Serialization;

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

    public Task<LocalWorkspace> CreateAsync(VerifiedMaterialization materialization, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(materialization);
        return CreateFromMaterializationAsync(materialization, accountId: null, cancellationToken);
    }

    public Task<LocalWorkspace> CreateAsync(
        VerifiedMaterialization materialization,
        string? accountId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(materialization);
        return CreateFromMaterializationAsync(materialization, accountId, cancellationToken);
    }

    private async Task<LocalWorkspace> CreateFromMaterializationAsync(
        VerifiedMaterialization materialization,
        string? accountId,
        CancellationToken cancellationToken)
    {
        var probe = await _probeService.ProbeAsync(
            materialization.OutputRoot,
            new DataSetProbeOptions(IncludeLocalPaths: true),
            cancellationToken).ConfigureAwait(false);
        var sourceRoot = probe.SourceRoot
            ?? throw new InvalidDataException("The materialized workspace probe did not retain its source root.");
        var manifestHash = await FileHashing.ComputeSha256Async(materialization.Result.ManifestPath, cancellationToken).ConfigureAwait(false);
        var provenance = new MaterializationProvenance(
            materialization.Result.SourceSnapshotId,
            materialization.Result.WorkspaceId,
            materialization.Result.BackendId,
            materialization.Result.BackendVersion,
            materialization.Result.BackendSha256,
            manifestHash,
            materialization.Result.KeyExtractionProfileId,
            materialization.Result.ProcessVersion,
            materialization.Result.ProcessImageSha256,
            materialization.Result.WcdbModuleSha256,
            materialization.Result.AccountSidFingerprint);
        var dataSet = string.IsNullOrWhiteSpace(accountId)
            ? probe.DataSet
            : new WeChatDataSet(
                probe.DataSet.DataSetId,
                accountId,
                probe.DataSet.Databases,
                provenance.SourceSnapshotId,
                probe.DataSet.AdapterId);
        return new LocalWorkspace(
            ComputeWorkspaceId(sourceRoot, probe.DataSet.DataSetId),
            sourceRoot,
            dataSet,
            DateTimeOffset.UtcNow,
            probe.Issues,
            probe.AdapterCandidates,
            provenance);
    }

    private static string ComputeWorkspaceId(string sourceRoot, string dataSetId)
    {
        var canonical = Encoding.UTF8.GetBytes(Path.GetFullPath(sourceRoot) + "|" + dataSetId);
        return "workspace-" + Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant()[..16];
    }
}
