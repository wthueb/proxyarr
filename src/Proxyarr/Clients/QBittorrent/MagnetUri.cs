namespace Proxyarr.Clients.QBittorrent;

/// <summary>
/// Extracts the BitTorrent v1 info-hash from a magnet URI's <c>xt=urn:btih:</c> parameter, accepting
/// both the 40-char hex and 32-char base32 encodings qBittorrent understands. v2-only magnets
/// (<c>urn:btmh:</c>) and malformed input return false.
/// </summary>
public static class MagnetUri
{
    public static bool TryGetInfoHash(string magnet, out string infoHashHex)
    {
        infoHashHex = "";

        var query = magnet.IndexOf('?');
        if (query < 0)
        {
            return false;
        }

        const string prefix = "urn:btih:";
        foreach (var pair in magnet[(query + 1)..].Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0 || !pair[..eq].Equals("xt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            if (
                value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && TryNormalizeHash(value[prefix.Length..], out infoHashHex)
            )
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryNormalizeHash(string hash, out string infoHashHex)
    {
        infoHashHex = "";

        if (hash.Length == 40 && hash.All(Uri.IsHexDigit))
        {
            infoHashHex = hash.ToLowerInvariant();
            return true;
        }

        if (hash.Length == 32 && TryDecodeBase32(hash, out var bytes))
        {
            infoHashHex = Convert.ToHexStringLower(bytes);
            return true;
        }

        return false;
    }

    /// <summary>Decodes 32 RFC-4648 base32 characters into the 20-byte v1 info-hash.</summary>
    private static bool TryDecodeBase32(string input, out byte[] bytes)
    {
        bytes = new byte[20];
        var buffer = 0;
        var bitsLeft = 0;
        var index = 0;

        foreach (var c in input)
        {
            int value;
            if (c is >= 'A' and <= 'Z')
            {
                value = c - 'A';
            }
            else if (c is >= 'a' and <= 'z')
            {
                value = c - 'a';
            }
            else if (c is >= '2' and <= '7')
            {
                value = c - '2' + 26;
            }
            else
            {
                return false;
            }

            buffer = (buffer << 5) | value;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                if (index >= bytes.Length)
                {
                    return false;
                }

                bytes[index++] = (byte)((buffer >> bitsLeft) & 0xFF);
            }
        }

        return index == bytes.Length;
    }
}
