namespace WeChatVoice.KeyAcquisition.Ports;

/// <summary>
/// Validates a candidate without retaining, serializing, or logging it.
/// Implementations must use a database-authentication primitive, not a
/// plaintext-header coincidence.
/// </summary>
public interface IDatabaseKeyValidator
{
    string Id { get; }

    DatabaseKeyValidationResult ValidateFirstPage(
        ReadOnlySpan<byte> encryptedFirstPage,
        ReadOnlySpan<byte> candidateKey);
}

public readonly record struct DatabaseKeyValidationResult(
    bool IsValid,
    DatabaseKeyValidationFailure Failure)
{
    public static DatabaseKeyValidationResult Valid { get; } = new(true, DatabaseKeyValidationFailure.None);

    public static DatabaseKeyValidationResult Invalid(DatabaseKeyValidationFailure failure) => new(false, failure);
}

public enum DatabaseKeyValidationFailure
{
    None,
    InvalidPageLength,
    InvalidKeyLength,
    AuthenticationMismatch,
}
