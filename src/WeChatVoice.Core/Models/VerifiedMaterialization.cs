namespace WeChatVoice.Core.Models;

/// <summary>
/// A materialization result that passed source mapping, output traversal,
/// SQLite validation, and manifest creation.
/// </summary>
public sealed record VerifiedMaterialization
{
    public VerifiedMaterialization(MaterializationResult Result, DateTimeOffset VerifiedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(Result);
        this.Result = Result;
        this.VerifiedAtUtc = VerifiedAtUtc.ToUniversalTime();
    }

    public MaterializationResult Result { get; }

    public string OutputRoot => Result.OutputRoot;

    public DateTimeOffset VerifiedAtUtc { get; }
}
