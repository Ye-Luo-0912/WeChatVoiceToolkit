using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;

namespace WeChatVoice.Workflows.Tests;

/// <summary>
/// Shared helpers: a fake workspace verifier so voice workflows run against a
/// canned workspace, and a workspace JSON writer for the opener's read path.
/// </summary>
public static class TestFixtures
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static LocalWorkspace MakeWorkspace()
        => new LocalWorkspace(
            "workspace-fake",
            "C:\\fake\\root",
            new WeChatDataSet("dataset-fake", FakeBackend.AccountId, [], "snapshot-fake", "fake-adapter"),
            DateTimeOffset.UtcNow,
            Issues: [],
            AdapterCandidates: [],
            Provenance: new MaterializationProvenance(
                "snapshot-fake",
                "materialized-fake",
                "fake-backend",
                "fake-v1",
                "backend-hash",
                "manifest-hash",
                "fake-profile",
                "4.1.11.55",
                "ac599744a7ce7b65640ebe18c939c0d4e4a06cd039d89cddee7f1e9afc56875d",
                "ab925b9428239def44b252d970c337034d75e66b27eb5529633dc10669fc796a",
                "sid-fingerprint"));

    /// <summary>Writes a parseable workspace JSON file the opener can read.</summary>
    public static string WriteWorkspaceFile(string directory, LocalWorkspace workspace)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "local-workspace.json");
        File.WriteAllText(path, JsonSerializer.Serialize(workspace, JsonOptions));
        return path;
    }

    /// <summary>Verifier stub: returns a canned verified workspace for any input.</summary>
    public sealed class FakeWorkspaceVerifier(VerifiedLocalWorkspace result) : ILocalWorkspaceVerifier
    {
        public Task<VerifiedLocalWorkspace> VerifyAsync(LocalWorkspace workspace, CancellationToken cancellationToken)
            => Task.FromResult(result);
    }

    public static VerifiedLocalWorkspace Verified(LocalWorkspace workspace)
        => new(workspace, DateTimeOffset.UtcNow);
}
