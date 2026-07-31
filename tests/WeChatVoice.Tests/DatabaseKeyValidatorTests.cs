using WeChatVoice.KeyAcquisition.Ports;
using WeChatVoice.KeyAcquisition.Validation;

namespace WeChatVoice.Tests;

public sealed class DatabaseKeyValidatorTests
{
    private static readonly byte[] ExpectedPageHmac = Convert.FromHexString(
        "d8aec24fc2691938b3cf3b995f8fddc758b36b1906dfc8611d36dedfdc323ae3" +
        "d01f97050de0b665476ba9d63bd15087ebdcecccdc6a492386ddce76e7ec8d9a");
    private static readonly byte[] ExpectedVersion3PageHmac = Convert.FromHexString(
        "45eb4a4b28ee3eaca836a53f9e0a9a4f8bfba0c9");

    [Fact]
    public void ValidateFirstPage_accepts_the_fixed_sqlcipher4_hmac_vector_without_mutating_inputs()
    {
        var validator = new WeixinWindows4SqlCipherKeyValidator();
        var page = CreateAuthenticatedPage();
        var key = Enumerable.Range(0, WeixinWindows4SqlCipherKeyValidator.KeySize).Select(static value => (byte)value).ToArray();
        var originalPage = page.ToArray();
        var originalKey = key.ToArray();

        var result = validator.ValidateFirstPage(page, key);

        Assert.True(result.IsValid);
        Assert.Equal(DatabaseKeyValidationFailure.None, result.Failure);
        Assert.Equal(WeixinWindows4SqlCipherKeyValidator.EncryptionProfileId, result.EncryptionProfileId);
        Assert.Equal(originalPage, page);
        Assert.Equal(originalKey, key);
    }

    [Fact]
    public void ValidateFirstPage_accepts_the_fixed_compatibility3_page4096_hmac_vector()
    {
        var validator = new WeixinWindows4SqlCipher3Page4096KeyValidator();
        var page = CreateVersion3AuthenticatedPage();
        var key = Enumerable.Range(0, WeixinWindows4SqlCipher3Page4096KeyValidator.KeySize).Select(static value => (byte)value).ToArray();

        var result = validator.ValidateFirstPage(page, key);

        Assert.True(result.IsValid);
        Assert.Equal(DatabaseKeyValidationFailure.None, result.Failure);
        Assert.Equal(WeixinWindows4SqlCipher3Page4096KeyValidator.EncryptionProfileId, result.EncryptionProfileId);
    }

    [Fact]
    public void Exact_41155_validator_reports_the_matching_encryption_profile()
    {
        var validator = new WeixinWindows41155SqlCipherKeyValidator();
        var key = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();

        var version3 = validator.ValidateFirstPage(CreateVersion3AuthenticatedPage(), key);
        var version4 = validator.ValidateFirstPage(CreateAuthenticatedPage(), key);

        Assert.Equal(WeixinWindows4SqlCipher3Page4096KeyValidator.EncryptionProfileId, version3.EncryptionProfileId);
        Assert.Equal(WeixinWindows4SqlCipherKeyValidator.EncryptionProfileId, version4.EncryptionProfileId);
    }

    [Fact]
    public void ValidateFirstPage_rejects_a_wrong_key_and_authenticated_page_tampering()
    {
        var validator = new WeixinWindows4SqlCipherKeyValidator();
        var page = CreateAuthenticatedPage();
        var wrongKey = Enumerable.Repeat((byte)0x5A, WeixinWindows4SqlCipherKeyValidator.KeySize).ToArray();

        var wrongKeyResult = validator.ValidateFirstPage(page, wrongKey);
        page[128] ^= 0x01;
        var tamperedPageResult = validator.ValidateFirstPage(
            page,
            Enumerable.Range(0, WeixinWindows4SqlCipherKeyValidator.KeySize).Select(static value => (byte)value).ToArray());

        Assert.False(wrongKeyResult.IsValid);
        Assert.Equal(DatabaseKeyValidationFailure.AuthenticationMismatch, wrongKeyResult.Failure);
        Assert.False(tamperedPageResult.IsValid);
        Assert.Equal(DatabaseKeyValidationFailure.AuthenticationMismatch, tamperedPageResult.Failure);
    }

    [Theory]
    [InlineData(4095, 32, DatabaseKeyValidationFailure.InvalidPageLength)]
    [InlineData(4096, 31, DatabaseKeyValidationFailure.InvalidKeyLength)]
    public void ValidateFirstPage_rejects_unsupported_shapes(int pageLength, int keyLength, DatabaseKeyValidationFailure expectedFailure)
    {
        var result = new WeixinWindows4SqlCipherKeyValidator().ValidateFirstPage(
            new byte[pageLength],
            new byte[keyLength]);

        Assert.False(result.IsValid);
        Assert.Equal(expectedFailure, result.Failure);
    }

    private static byte[] CreateAuthenticatedPage()
    {
        var page = new byte[WeixinWindows4SqlCipherKeyValidator.PageSize];
        for (var index = 0; index < page.Length; index++)
        {
            page[index] = (byte)((index * 31 + 17) % 256);
        }

        for (var index = 0; index < WeixinWindows4SqlCipherKeyValidator.SaltSize; index++)
        {
            page[index] = (byte)(0xA0 + index);
        }

        ExpectedPageHmac.CopyTo(page, page.Length - ExpectedPageHmac.Length);
        return page;
    }

    private static byte[] CreateVersion3AuthenticatedPage()
    {
        var page = new byte[WeixinWindows4SqlCipher3Page4096KeyValidator.PageSize];
        for (var index = 0; index < page.Length; index++)
        {
            page[index] = (byte)((index * 31 + 17) % 256);
        }

        for (var index = 0; index < WeixinWindows4SqlCipher3Page4096KeyValidator.SaltSize; index++)
        {
            page[index] = (byte)(0xA0 + index);
        }

        ExpectedVersion3PageHmac.CopyTo(
            page,
            page.Length - WeixinWindows4SqlCipher3Page4096KeyValidator.ReservedSize + WeixinWindows4SqlCipher3Page4096KeyValidator.SaltSize);
        return page;
    }
}
