using System.Security.Cryptography;
using System.Text;

namespace Proxyarr.Tests.Support;

/// <summary>
/// Builds a minimal, syntactically valid single-file .torrent (BitTorrent v1) and returns its bytes
/// alongside the v1 info-hash (SHA-1 of the raw <c>info</c> dict). The construction mirrors the
/// integration project's helper, so the bencode parser under test must reproduce the same hash.
/// </summary>
public static class TestTorrent
{
    public static (byte[] Bytes, string InfoHashHex) Create(string name)
    {
        using var info = new MemoryStream();
        WriteRaw(info, "d");
        WriteString(info, "length");
        WriteRaw(info, "i1024e");
        WriteString(info, "name");
        WriteString(info, name);
        WriteString(info, "piece length");
        WriteRaw(info, "i16384e");
        WriteString(info, "pieces");
        WriteBytes(info, RandomNumberGenerator.GetBytes(20));
        WriteRaw(info, "e");
        var infoBytes = info.ToArray();

        using var torrent = new MemoryStream();
        WriteRaw(torrent, "d");
        WriteString(torrent, "announce");
        WriteString(torrent, "http://tracker.invalid:6969/announce");
        WriteString(torrent, "info");
        torrent.Write(infoBytes);
        WriteRaw(torrent, "e");

        return (torrent.ToArray(), Convert.ToHexStringLower(SHA1.HashData(infoBytes)));
    }

    private static void WriteRaw(Stream stream, string value) =>
        stream.Write(Encoding.ASCII.GetBytes(value));

    private static void WriteString(Stream stream, string value) =>
        WriteBytes(stream, Encoding.UTF8.GetBytes(value));

    private static void WriteBytes(Stream stream, byte[] value)
    {
        stream.Write(Encoding.ASCII.GetBytes($"{value.Length}:"));
        stream.Write(value);
    }
}
