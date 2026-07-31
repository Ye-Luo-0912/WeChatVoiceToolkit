using WeChatVoice.KeyAcquisition.Ports;
using WeChatVoice.KeyAcquisition.Validation;

namespace WeChatVoice.Tests;

public sealed class DatabaseKeyValidatorTests
{
    private static readonly byte[] ExpectedPageHmac = Convert.FromHexString(
        "d8aec24fc2691938b3cf3b995f8fddc758b36b1906dfc8611d36dedfdc323ae3" +
        "d01f97050de0b665476ba9d63bd15087ebdcecccdc6a492386ddce76e7ec8d9a");

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
        Assert.Equal(originalPage, page);
        Assert.Equal(originalKey, key);
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
}
