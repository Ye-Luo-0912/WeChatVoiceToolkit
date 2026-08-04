using System.Diagnostics;
using System.Security.Cryptography;

namespace WeChatVoice.Tests;

public sealed class SqlCipherWorkerTests
{
    private const string FixtureEncryptionProfileId = "weixin-windows-4.sqlcipher4-page4096-hmac-sha512-v1";

    [Fact]
    public async Task Worker_self_test_loads_sqlcipher_without_reading_a_key_or_database()
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "WeChatVoice.SqlCipherWorker.dll");
        Assert.True(File.Exists(worker), worker);

        var result = await RunDotnetAsync(
            worker,
            ["--self-test"],
            ReadOnlyMemory<byte>.Empty,
            throwOnFailure: false);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("WCV1", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Worker_materializes_a_synthetic_cipher_database_without_persisting_the_key()
    {
        using var temporary = new TestTemporaryDirectory();
        var encrypted = temporary.GetPath("encrypted.db");
        var plaintext = temporary.GetPath("output", "plaintext.db");
        var fixture = Path.Combine(AppContext.BaseDirectory, "WeChatVoice.SqlCipherFixture.dll");
        var worker = Path.Combine(AppContext.BaseDirectory, "WeChatVoice.SqlCipherWorker.dll");
        Assert.True(File.Exists(fixture), fixture);
        Assert.True(File.Exists(worker), worker);

        Assert.Equal(0, await RunDotnetAsync(fixture, ["--output", encrypted], ReadOnlyMemory<byte>.Empty));
        var key = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        var envelope = new byte[5 + key.Length];
        "WCV1"u8.CopyTo(envelope);
        envelope[4] = 32;
        key.CopyTo(envelope, 5);
        CryptographicOperations.ZeroMemory(key);

        var exitCode = await RunDotnetAsync(worker, WorkerArguments(encrypted, plaintext), envelope);
        CryptographicOperations.ZeroMemory(envelope);

        Assert.Equal(0, exitCode);
        var header = new byte[16];
        await using (var stream = new FileStream(plaintext, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(16, await stream.ReadAsync(header));
        }

        Assert.Equal("SQLite format 3\0", System.Text.Encoding.ASCII.GetString(header));
        Assert.DoesNotContain("WCV1", await File.ReadAllTextAsync(plaintext));
    }

    [Fact]
    public async Task Worker_rejects_malformed_or_trailing_key_envelopes_without_creating_output()
    {
        using var temporary = new TestTemporaryDirectory();
        var encrypted = temporary.GetPath("encrypted.db");
        var output = temporary.GetPath("output.db");
        var fixture = Path.Combine(AppContext.BaseDirectory, "WeChatVoice.SqlCipherFixture.dll");
        var worker = Path.Combine(AppContext.BaseDirectory, "WeChatVoice.SqlCipherWorker.dll");
        Assert.Equal(0, await RunDotnetAsync(fixture, ["--output", encrypted], ReadOnlyMemory<byte>.Empty));

        var malformed = await RunDotnetAsync(worker, WorkerArguments(encrypted, output), "WCV1"u8.ToArray(), throwOnFailure: false);
        Assert.Equal(1, malformed.ExitCode);
        Assert.False(File.Exists(output));
        Assert.DoesNotContain("WCV1", malformed.StandardError, StringComparison.Ordinal);

        var key = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        var envelope = new byte[5 + key.Length + 1];
        "WCV1"u8.CopyTo(envelope);
        envelope[4] = 32;
        key.CopyTo(envelope, 5);
        envelope[^1] = 0x7F;
        CryptographicOperations.ZeroMemory(key);
        var trailing = await RunDotnetAsync(worker, WorkerArguments(encrypted, output), envelope, throwOnFailure: false);
        CryptographicOperations.ZeroMemory(envelope);
        Assert.Equal(1, trailing.ExitCode);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Worker_rejects_a_wrong_key_and_preserves_an_existing_destination()
    {
        using var temporary = new TestTemporaryDirectory();
        var encrypted = temporary.GetPath("encrypted.db");
        var output = temporary.GetPath("output.db");
        var fixture = Path.Combine(AppContext.BaseDirectory, "WeChatVoice.SqlCipherFixture.dll");
        var worker = Path.Combine(AppContext.BaseDirectory, "WeChatVoice.SqlCipherWorker.dll");
        Assert.Equal(0, await RunDotnetAsync(fixture, ["--output", encrypted], ReadOnlyMemory<byte>.Empty));
        await File.WriteAllBytesAsync(output, [1, 2, 3]);

        var wrongKey = Enumerable.Repeat((byte)0xA5, 32).ToArray();
        var envelope = new byte[5 + wrongKey.Length];
        "WCV1"u8.CopyTo(envelope);
        envelope[4] = 32;
        wrongKey.CopyTo(envelope, 5);
        CryptographicOperations.ZeroMemory(wrongKey);
        var result = await RunDotnetAsync(worker, WorkerArguments(encrypted, output), envelope, throwOnFailure: false);
        CryptographicOperations.ZeroMemory(envelope);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(output));
        Assert.DoesNotContain("A5A5", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<int> RunDotnetAsync(string assembly, IReadOnlyList<string> arguments, ReadOnlyMemory<byte> stdin)
        => (await RunDotnetAsync(assembly, arguments, stdin, throwOnFailure: true)).ExitCode;

    private static string[] WorkerArguments(string input, string output) =>
        ["--input", input, "--output", output, "--encryption-profile", FixtureEncryptionProfileId];

    private static async Task<(int ExitCode, string StandardError)> RunDotnetAsync(
        string assembly,
        IReadOnlyList<string> arguments,
        ReadOnlyMemory<byte> stdin,
        bool throwOnFailure)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(assembly);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start SQLCipher test process.");
        if (!stdin.IsEmpty)
        {
            await process.StandardInput.BaseStream.WriteAsync(stdin);
        }

        process.StandardInput.Close();
        _ = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (throwOnFailure && process.ExitCode != 0 && error.Length > 0)
        {
            throw new InvalidOperationException($"Child process failed: {error.Trim()}");
        }

        return (process.ExitCode, error);
    }
}
