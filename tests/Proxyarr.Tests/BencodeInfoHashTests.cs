using System.Security.Cryptography;
using System.Text;
using Proxyarr.Clients.QBittorrent;
using Proxyarr.Tests.Support;

namespace Proxyarr.Tests;

public class BencodeInfoHashTests
{
    [Fact]
    public void Computes_the_info_hash_of_a_well_formed_torrent()
    {
        var (bytes, expectedHash) = TestTorrent.Create("proxyarr-movie");

        Assert.True(BencodeInfoHash.TryComputeV1(bytes, out var hash));
        Assert.Equal(expectedHash, hash);
    }

    [Fact]
    public void Handles_an_info_dict_that_is_not_the_first_key()
    {
        // 'announce' and 'comment' precede 'info'; the parser must skip them and still slice info.
        var info = "d4:name5:movie12:piece lengthi16384e6:pieces20:"u8
            .ToArray()
            .Concat(RandomNumberGenerator.GetBytes(20))
            .Concat("e"u8.ToArray())
            .ToArray();
        var torrent = BuildTorrent(("announce", "udp://x"), ("comment", "note"), info);

        var expected = Convert.ToHexStringLower(SHA1.HashData(info));
        Assert.True(BencodeInfoHash.TryComputeV1(torrent, out var hash));
        Assert.Equal(expected, hash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-bencode")]
    [InlineData("l4:infoe")] // top level is a list, not a dict
    [InlineData("d4:name5:movie")] // no info key, and unterminated
    public void Rejects_malformed_input(string raw)
    {
        Assert.False(BencodeInfoHash.TryComputeV1(Encoding.ASCII.GetBytes(raw), out var hash));
        Assert.Equal("", hash);
    }

    [Fact]
    public void Rejects_a_truncated_string_length()
    {
        // Declares a 99-byte string but supplies far fewer bytes.
        Assert.False(BencodeInfoHash.TryComputeV1("d99:short"u8, out _));
    }

    private static byte[] BuildTorrent(
        (string Key, string Value) first,
        (string Key, string Value) second,
        byte[] infoBytes
    )
    {
        using var stream = new MemoryStream();
        Write(stream, "d");
        WriteStr(stream, first.Key);
        WriteStr(stream, first.Value);
        WriteStr(stream, second.Key);
        WriteStr(stream, second.Value);
        WriteStr(stream, "info");
        stream.Write(infoBytes);
        Write(stream, "e");
        return stream.ToArray();
    }

    private static void Write(Stream s, string v) => s.Write(Encoding.ASCII.GetBytes(v));

    private static void WriteStr(Stream s, string v)
    {
        var bytes = Encoding.UTF8.GetBytes(v);
        s.Write(Encoding.ASCII.GetBytes($"{bytes.Length}:"));
        s.Write(bytes);
    }
}
