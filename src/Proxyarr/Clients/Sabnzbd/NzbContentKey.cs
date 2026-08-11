using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace Proxyarr.Clients.Sabnzbd;

/// <summary>
/// Derives a stable content key for an NZB from the sorted set of its segment message-IDs, hashed
/// with SHA-256. Because the message-IDs identify the actual Usenet articles, the same release
/// fetched from different indexers produces the same key and dedupes. When the XML can't be parsed
/// (or carries no segments), it falls back to <c>raw:</c> + SHA-256 of the bytes so a key is always
/// produced — that path simply won't cross-indexer dedupe.
/// </summary>
public static class NzbContentKey
{
    public static string Compute(ReadOnlySpan<byte> nzb) => Compute(nzb.ToArray());

    public static string Compute(byte[] nzb)
    {
        try
        {
            var ids = new SortedSet<string>(StringComparer.Ordinal);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
            };

            using var stream = new MemoryStream(nzb, writable: false);
            using var reader = XmlReader.Create(stream, settings);

            while (!reader.EOF)
            {
                if (
                    reader.NodeType == XmlNodeType.Element
                    && reader.LocalName.Equals("segment", StringComparison.OrdinalIgnoreCase)
                )
                {
                    // ReadElementContentAsString advances past the element itself, so we must not
                    // Read() again this iteration or we'd skip the following sibling segment.
                    var id = reader.ReadElementContentAsString().Trim();
                    if (id.Length > 0)
                    {
                        ids.Add(id);
                    }
                }
                else
                {
                    reader.Read();
                }
            }

            if (ids.Count == 0)
            {
                return RawKey(nzb);
            }

            var joined = Encoding.UTF8.GetBytes(string.Join('\n', ids));
            return Convert.ToHexStringLower(SHA256.HashData(joined));
        }
        catch (XmlException)
        {
            return RawKey(nzb);
        }
    }

    private static string RawKey(byte[] nzb) =>
        "raw:" + Convert.ToHexStringLower(SHA256.HashData(nzb));
}
