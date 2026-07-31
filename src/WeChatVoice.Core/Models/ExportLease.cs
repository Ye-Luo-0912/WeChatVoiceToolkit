namespace WeChatVoice.Core.Models;

public enum ExistingArtifactPolicy
{
    SkipIfHashMatches,
    VerifyOnly,
    Fail,
    Replace,
}

[Obsolete("Use ExistingArtifactPolicy.")]
public enum ExportExistingPolicy
{
    SkipIfHashMatches,
    VerifyOnly,
    Fail,
    Replace,
}

public sealed record ExportArtifact
{
    public ExportArtifact(string RelativePath, long ByteLength, string Sha256)
    {
        if (string.IsNullOrWhiteSpace(RelativePath) || Path.IsPathRooted(RelativePath))
        {
            throw new ArgumentException("An export artifact path must be relative.", nameof(RelativePath));
        }

        if (ByteLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ByteLength));
        }

        if (string.IsNullOrWhiteSpace(Sha256))
        {
            throw new ArgumentException("An export artifact SHA-256 is required.", nameof(Sha256));
        }

        this.RelativePath = RelativePath.Replace('\\', '/');
        this.ByteLength = ByteLength;
        this.Sha256 = Sha256;
    }

    public string RelativePath { get; }

    public long ByteLength { get; }

    public string Sha256 { get; }
}

public sealed class ExistingArtifactConflictException : IOException
{
    public ExistingArtifactConflictException(string message)
        : base(message)
    {
    }
}

public sealed class ExistingArtifactNeedsHashException : IOException
{
    public ExistingArtifactNeedsHashException(string message)
        : base(message)
    {
    }
}
