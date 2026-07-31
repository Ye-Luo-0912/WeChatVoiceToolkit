using System.Text;
using WeChatVoice.KeyBroker;

namespace WeChatVoice.Tests;

public sealed class HexKeyCandidateScannerTests
{
    [Fact]
    public void Scanner_decodes_exact_key_and_salt_across_chunk_boundary_without_retaining_an_address()
    {
        var expectedKey = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        var expectedSalt = Enumerable.Range(160, 16).Select(static value => (byte)value).ToArray();
        var pattern = Encoding.ASCII.GetBytes($"x'{Convert.ToHexString(expectedKey)}{Convert.ToHexString(expectedSalt)}'");
        byte[]? observedKey = null;
        byte[]? observedSalt = null;
        using var scanner = new HexKeyCandidateScanner((key, salt) =>
        {
            observedKey = key.ToArray();
            observedSalt = salt.ToArray();
            return true;
        });

        Assert.True(scanner.ProcessChunk(pattern[..37], startsRegion: true));
        Assert.True(scanner.ProcessChunk(pattern[37..], startsRegion: false));

        Assert.Equal(1, scanner.CandidateCount);
        Assert.Equal(expectedKey, observedKey);
        Assert.Equal(expectedSalt, observedSalt);
    }

    [Fact]
    public void Scanner_rejects_unterminated_or_non_hex_values_and_resets_at_region_boundaries()
    {
        var keyText = new string('a', 64);
        var observed = 0;
        using var scanner = new HexKeyCandidateScanner((_, _) =>
        {
            observed++;
            return true;
        });

        Assert.True(scanner.ProcessChunk(Encoding.ASCII.GetBytes($"x'{keyText}"), startsRegion: true));
        Assert.True(scanner.ProcessChunk([(byte)'\''], startsRegion: true));
        Assert.True(scanner.ProcessChunk(Encoding.ASCII.GetBytes($"x'{keyText[..63]}g'"), startsRegion: true));

        Assert.Equal(0, observed);
        Assert.Equal(0, scanner.CandidateCount);
    }

    [Fact]
    public void Scanner_stops_after_the_fixed_candidate_limit()
    {
        var pattern = Encoding.ASCII.GetBytes($"x'{new string('0', 64)}'");
        var data = new byte[pattern.Length * (HexKeyCandidateScanner.MaximumCandidates + 1)];
        for (var index = 0; index < HexKeyCandidateScanner.MaximumCandidates + 1; index++)
        {
            pattern.CopyTo(data, index * pattern.Length);
        }

        using var scanner = new HexKeyCandidateScanner((_, _) => true);
        var shouldContinue = scanner.ProcessChunk(data, startsRegion: true);

        Assert.False(shouldContinue);
        Assert.Equal(HexKeyCandidateScanner.MaximumCandidates + 1, scanner.CandidateCount);
    }
}
