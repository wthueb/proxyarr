using Proxyarr.Clients.QBittorrent;

namespace Proxyarr.Tests;

public class MagnetUriTests
{
    [Fact]
    public void Extracts_a_40_char_hex_hash()
    {
        var magnet = "magnet:?xt=urn:btih:0123456789ABCDEF0123456789abcdef01234567&dn=Some.Movie";

        Assert.True(MagnetUri.TryGetInfoHash(magnet, out var hash));
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", hash);
    }

    [Fact]
    public void Extracts_and_decodes_a_32_char_base32_hash()
    {
        // Base32 of the 20 bytes 0x00..0x13 -> known hex.
        var bytes = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
        var base32 = Base32Encode(bytes);
        var expectedHex = Convert.ToHexStringLower(bytes);

        Assert.True(MagnetUri.TryGetInfoHash($"magnet:?xt=urn:btih:{base32}&dn=x", out var hash));
        Assert.Equal(expectedHex, hash);
    }

    [Fact]
    public void Prefers_btih_even_when_a_v2_xt_is_present_first()
    {
        var magnet =
            "magnet:?xt=urn:btmh:1220caf1&xt=urn:btih:0123456789ABCDEF0123456789abcdef01234567";

        Assert.True(MagnetUri.TryGetInfoHash(magnet, out var hash));
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", hash);
    }

    [Theory]
    [InlineData("not-a-magnet")]
    [InlineData("magnet:?dn=only-a-name")]
    [InlineData("magnet:?xt=urn:btmh:1220deadbeef")] // v2-only
    [InlineData("magnet:?xt=urn:btih:tooshort")]
    public void Returns_false_for_unusable_magnets(string magnet)
    {
        Assert.False(MagnetUri.TryGetInfoHash(magnet, out var hash));
        Assert.Equal("", hash);
    }

    private static string Base32Encode(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new System.Text.StringBuilder();
        int buffer = 0,
            bitsLeft = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                output.Append(alphabet[(buffer >> bitsLeft) & 0x1F]);
            }
        }

        if (bitsLeft > 0)
        {
            output.Append(alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
        }

        return output.ToString();
    }
}
