using System.Text;
using Proxyarr.Clients.Sabnzbd;

namespace Proxyarr.Tests;

public class NzbContentKeyTests
{
    private const string Nzb = """
        <?xml version="1.0" encoding="UTF-8"?>
        <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
          <file poster="a@b.c" date="1" subject="thing">
            <segments>
              <segment bytes="1" number="1">first@example.invalid</segment>
              <segment bytes="1" number="2">second@example.invalid</segment>
            </segments>
          </file>
        </nzb>
        """;

    [Fact]
    public void Same_segments_produce_the_same_key()
    {
        var a = NzbContentKey.Compute(Encoding.UTF8.GetBytes(Nzb));
        var b = NzbContentKey.Compute(Encoding.UTF8.GetBytes(Nzb));

        Assert.Equal(a, b);
        Assert.DoesNotContain("raw:", a);
    }

    [Fact]
    public void Segment_order_does_not_change_the_key()
    {
        var reordered = Nzb.Replace(
            """
                  <segment bytes="1" number="1">first@example.invalid</segment>
                  <segment bytes="1" number="2">second@example.invalid</segment>
            """,
            """
                  <segment bytes="1" number="2">second@example.invalid</segment>
                  <segment bytes="1" number="1">first@example.invalid</segment>
            """
        );

        Assert.Equal(
            NzbContentKey.Compute(Encoding.UTF8.GetBytes(Nzb)),
            NzbContentKey.Compute(Encoding.UTF8.GetBytes(reordered))
        );
    }

    [Fact]
    public void Different_namespace_prefix_still_dedupes_on_message_ids()
    {
        // Same message-IDs, a namespace prefix and different attribute/whitespace: same key.
        var prefixed = """
            <?xml version="1.0"?>
            <n:nzb xmlns:n="http://example/other">
              <n:file><n:segments>
                <n:segment number="2">second@example.invalid</n:segment>
                <n:segment number="1">first@example.invalid</n:segment>
              </n:segments></n:file>
            </n:nzb>
            """;

        Assert.Equal(
            NzbContentKey.Compute(Encoding.UTF8.GetBytes(Nzb)),
            NzbContentKey.Compute(Encoding.UTF8.GetBytes(prefixed))
        );
    }

    [Fact]
    public void Different_segments_produce_different_keys()
    {
        var other = Nzb.Replace("first@example.invalid", "different@example.invalid");

        Assert.NotEqual(
            NzbContentKey.Compute(Encoding.UTF8.GetBytes(Nzb)),
            NzbContentKey.Compute(Encoding.UTF8.GetBytes(other))
        );
    }

    [Fact]
    public void Falls_back_to_raw_hash_when_xml_is_unparseable()
    {
        var garbage = Encoding.UTF8.GetBytes("this is not < xml at > all &");

        var key = NzbContentKey.Compute(garbage);

        Assert.StartsWith("raw:", key);
        Assert.Equal(key, NzbContentKey.Compute(garbage));
    }

    [Fact]
    public void Falls_back_to_raw_hash_when_there_are_no_segments()
    {
        var empty = Encoding.UTF8.GetBytes("<nzb></nzb>");

        Assert.StartsWith("raw:", NzbContentKey.Compute(empty));
    }
}
