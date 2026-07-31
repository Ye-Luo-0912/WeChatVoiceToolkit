using System.Buffers.Binary;
using System.Security.Cryptography;
using WeChatVoice.KeyAcquisition.Ports;

namespace WeChatVoice.KeyAcquisition.Validation;

/// <summary>
/// Validates the WCDB configuration produced by applying SQLCipher
/// compatibility 3 and then overriding cipher_page_size to 4096. The HMAC is
/// SHA-1, its 48-byte reserve contains IV + HMAC + padding, and the raw key
/// still uses the database salt from page one.
/// </summary>
public sealed class WeixinWindows4SqlCipher3Page4096KeyValidator : IDatabaseKeyValidator
{
    public const string EncryptionProfileId = "weixin-windows-4.sqlcipher3-page4096-hmac-sha1-v1";
    public const int PageSize = 4096;
    public const int KeySize = 32;
    public const int SaltSize = 16;
    public const int ReservedSize = 48;
    public const int HmacSize = 20;
    public const int KdfIterations = 2;
    public const byte HmacSaltMask = 0x3A;

    private const int PageNumberSize = sizeof(uint);
    private const int AuthenticatedPageEnd = PageSize - ReservedSize + SaltSize;
    private const int AuthenticatedPageLength = AuthenticatedPageEnd - SaltSize;
    private const int StoredHmacOffset = PageSize - ReservedSize + SaltSize;

    public string Id => EncryptionProfileId;

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
                HashAlgorithmName.SHA1);

            encryptedFirstPage.Slice(SaltSize, AuthenticatedPageLength).CopyTo(authenticatedData);
            BinaryPrimitives.WriteUInt32LittleEndian(authenticatedData[AuthenticatedPageLength..], 1);
            HMACSHA1.HashData(hmacKey, authenticatedData, computedHmac);

            var storedHmac = encryptedFirstPage.Slice(StoredHmacOffset, HmacSize);
            return CryptographicOperations.FixedTimeEquals(computedHmac, storedHmac)
                ? DatabaseKeyValidationResult.ValidFor(EncryptionProfileId)
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
