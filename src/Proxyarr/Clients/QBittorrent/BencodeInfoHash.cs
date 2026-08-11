using System.Security.Cryptography;

namespace Proxyarr.Clients.QBittorrent;

/// <summary>
/// Computes a BitTorrent v1 info-hash from a raw <c>.torrent</c> file by locating the byte range of
/// the top-level <c>info</c> dictionary and SHA-1'ing it verbatim — the same bytes qBittorrent
/// hashes, so the result matches the hash a torrent is tracked under. A tiny hand-rolled bencode
/// walker (no dependency); malformed input returns false so the caller forwards the add untouched.
/// </summary>
public static class BencodeInfoHash
{
    private const int MaxDepth = 32;

    public static bool TryComputeV1(ReadOnlySpan<byte> torrent, out string infoHashHex)
    {
        infoHashHex = "";

        var pos = 0;
        if (pos >= torrent.Length || torrent[pos] != (byte)'d')
        {
            return false;
        }

        pos++; // consume the top-level dict's 'd'
        while (pos < torrent.Length && torrent[pos] != (byte)'e')
        {
            if (!TryReadString(torrent, ref pos, out var keyStart, out var keyLength))
            {
                return false;
            }

            var valueStart = pos;
            if (!TrySkip(torrent, ref pos, 0))
            {
                return false;
            }

            if (torrent.Slice(keyStart, keyLength).SequenceEqual("info"u8))
            {
                var infoDict = torrent[valueStart..pos];
                infoHashHex = Convert.ToHexStringLower(SHA1.HashData(infoDict));
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads a bencoded string header <c>len:</c>, yielding the byte slice and advancing past it.</summary>
    private static bool TryReadString(
        ReadOnlySpan<byte> data,
        ref int pos,
        out int start,
        out int length
    )
    {
        start = 0;
        length = 0;

        long len = 0;
        var anyDigit = false;
        var p = pos;
        while (p < data.Length && data[p] != (byte)':')
        {
            var b = data[p];
            if (b is < (byte)'0' or > (byte)'9')
            {
                return false;
            }

            len = (len * 10) + (b - (byte)'0');
            if (len > int.MaxValue)
            {
                return false;
            }

            anyDigit = true;
            p++;
        }

        if (!anyDigit || p >= data.Length)
        {
            return false;
        }

        p++; // consume ':'
        if (p + len > data.Length)
        {
            return false;
        }

        start = p;
        length = (int)len;
        pos = p + (int)len;
        return true;
    }

    /// <summary>Skips exactly one bencoded value starting at <paramref name="pos"/>.</summary>
    private static bool TrySkip(ReadOnlySpan<byte> data, ref int pos, int depth)
    {
        if (depth > MaxDepth || pos >= data.Length)
        {
            return false;
        }

        var c = data[pos];
        switch (c)
        {
            case (byte)'i':
                pos++;
                if (pos < data.Length && data[pos] == (byte)'-')
                {
                    pos++;
                }

                var anyDigit = false;
                while (pos < data.Length && data[pos] is >= (byte)'0' and <= (byte)'9')
                {
                    pos++;
                    anyDigit = true;
                }

                if (!anyDigit || pos >= data.Length || data[pos] != (byte)'e')
                {
                    return false;
                }

                pos++; // consume 'e'
                return true;

            case (byte)'l':
            case (byte)'d':
                pos++;
                while (pos < data.Length && data[pos] != (byte)'e')
                {
                    if (c == (byte)'d' && !TryReadString(data, ref pos, out _, out _))
                    {
                        return false;
                    }

                    if (c == (byte)'l' && !TrySkip(data, ref pos, depth + 1))
                    {
                        return false;
                    }

                    if (c == (byte)'d' && !TrySkip(data, ref pos, depth + 1))
                    {
                        return false;
                    }
                }

                if (pos >= data.Length)
                {
                    return false;
                }

                pos++; // consume 'e'
                return true;

            default:
                return c is >= (byte)'0' and <= (byte)'9'
                    && TryReadString(data, ref pos, out _, out _);
        }
    }
}
