using System.Text.Json;

namespace WeChatVoice.Tests;

public sealed class CliIntegrationTests
{
    [Fact]
    public async Task Snapshot_create_copies_files_and_emits_a_manifest_through_the_public_cli()
    {
        using var temporary = new TestTemporaryDirectory();
        var sourceDirectory = temporary.CreateDirectory("source");
        temporary.WriteFile(Path.Combine("source", "media", "voice.db-wal"), new byte[] { 1, 2, 3, 4 });
        var outputDirectory = temporary.GetPath("snapshot");

        var result = await ManagedProcessTestHarness.RunAssemblyAsync(
            "WeChatVoice.Cli.dll",
            standardInput: null,
            "snapshot",
            "create",
            "--source",
            sourceDirectory,
            "--output",
            outputDirectory,
            "--allow-live-source");

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.True(File.Exists(Path.Combine(outputDirectory, "media", "voice.db-wal")));
        Assert.True(File.Exists(Path.Combine(outputDirectory, ".wechatvoice", "snapshot-manifest.json")));

        using var resultDocument = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(Path.GetFullPath(outputDirectory), resultDocument.RootElement.GetProperty("snapshotDirectory").GetString());
        Assert.Equal(1, resultDocument.RootElement.GetProperty("files").GetArrayLength());
    }

    [Fact]
    public async Task Schema_probe_writes_a_schema_document_through_the_public_cli()
    {
        using var temporary = new TestTemporaryDirectory();
        var databasePath = temporary.GetPath("probe.db");
        await SqliteSchemaInspectorTests.CreateSampleDatabaseAsync(databasePath);
        var outputPath = temporary.GetPath("schema", "schema.json");

        var result = await ManagedProcessTestHarness.RunAssemblyAsync(
            "WeChatVoice.Cli.dll",
            standardInput: null,
            "schema",
            "probe",
            "--database",
            databasePath,
            "--output",
            outputPath);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.True(File.Exists(outputPath));

        using var outputDocument = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
        var objects = outputDocument.RootElement.GetProperty("objects").EnumerateArray().ToArray();
        Assert.Contains(objects, item => item.GetProperty("name").GetString() == "voice_records");
        Assert.Contains(objects, item => item.GetProperty("name").GetString() == "incoming_voice");

        using var resultDocument = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(Path.GetFullPath(outputPath), resultDocument.RootElement.GetProperty("outputPath").GetString());
        Assert.Equal(2, resultDocument.RootElement.GetProperty("objectCount").GetInt32());
    }

    [Fact]
    public async Task Workspace_materialize_rejects_external_backend_without_explicit_untrusted_opt_in()
    {
        using var temporary = new TestTemporaryDirectory();
        var result = await ManagedProcessTestHarness.RunAssemblyAsync(
            "WeChatVoice.Cli.dll",
            standardInput: null,
            "workspace",
            "materialize",
            "--snapshot-directory",
            temporary.GetPath("snapshot"),
            "--external-decryptor",
            temporary.GetPath("backend.exe"),
            "--output",
            temporary.GetPath("materialized"));

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("requires --allow-untrusted-backend", result.StandardError, StringComparison.Ordinal);
    }
}
