using System.Buffers.Binary;
using System.Security.Cryptography;
using WeChatVoice.KeyAcquisition.Ports;

namespace WeChatVoice.KeyAcquisition.Validation;

/// <summary>
/// Candidate-only validator for the observed Weixin Windows 4.x 4096-byte
/// SQLCipher page shape. This does not acquire keys, decrypt databases, or
/// constitute an enabled build Profile.
/// </summary>
public sealed class WeixinWindows4SqlCipherKeyValidator : IDatabaseKeyValidator
{
    public const int PageSize = 4096;
    public const int KeySize = 32;
    public const int SaltSize = 16;
    public const int ReservedSize = 80;
    public const int HmacSize = 64;
    public const int KdfIterations = 2;
    public const byte HmacSaltMask = 0x3A;

    private const int PageNumberSize = sizeof(uint);
    private const int AuthenticatedPageEnd = PageSize - ReservedSize + SaltSize;
    private const int AuthenticatedPageLength = AuthenticatedPageEnd - SaltSize;

    public string Id => "weixin-windows-4.sqlcipher4-page-hmac-sha512-v1";

    public DatabaseKeyValidationResult ValidateFirstPage(
        ReadOnlySpan<byte> encryptedFirstPage,
        ReadOnlySpan<byte> candidateKey)
    {
        if (encryptedFirstPage.Length != PageSize)
        {
            return DatabaseKeyValidationResult.Invalid(DatabaseKeyValidationFailure.InvalidPageLength);
        }

        if (candidateKey.Length != KeySize)
        {
            return DatabaseKeyValidationResult.Invalid(DatabaseKeyValidationFailure.InvalidKeyLength);
        }

        Span<byte> hmacSalt = stackalloc byte[SaltSize];
        Span<byte> hmacKey = stackalloc byte[KeySize];
        Span<byte> authenticatedData = stackalloc byte[AuthenticatedPageLength + PageNumberSize];
        Span<byte> computedHmac = stackalloc byte[HmacSize];
        try
        {
            for (var index = 0; index < SaltSize; index++)
            {
                hmacSalt[index] = (byte)(encryptedFirstPage[index] ^ HmacSaltMask);
            }

            Rfc2898DeriveBytes.Pbkdf2(
                candidateKey,
                hmacSalt,
                hmacKey,
                KdfIterations,
                HashAlgorithmName.SHA512);

            encryptedFirstPage.Slice(SaltSize, AuthenticatedPageLength).CopyTo(authenticatedData);
            BinaryPrimitives.WriteUInt32LittleEndian(authenticatedData[AuthenticatedPageLength..], 1);
            HMACSHA512.HashData(hmacKey, authenticatedData, computedHmac);

            var storedHmac = encryptedFirstPage[(PageSize - HmacSize)..];
            return CryptographicOperations.FixedTimeEquals(computedHmac, storedHmac)
                ? DatabaseKeyValidationResult.Valid
                : DatabaseKeyValidationResult.Invalid(DatabaseKeyValidationFailure.AuthenticationMismatch);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hmacSalt);
            CryptographicOperations.ZeroMemory(hmacKey);
            CryptographicOperations.ZeroMemory(authenticatedData);
            CryptographicOperations.ZeroMemory(computedHmac);
        }
    }
}
