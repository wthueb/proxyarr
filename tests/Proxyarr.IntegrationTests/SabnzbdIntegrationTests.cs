using System.Net;
using System.Text;
using System.Text.Json;
using Proxyarr.IntegrationTests.Support;

namespace Proxyarr.IntegrationTests;

/// <summary>
/// Exercises every proxied SABnzbd API mode against a real SABnzbd 5.0.4 container, mirroring
/// the call patterns of Radarr's download client.
/// </summary>
public sealed class SabnzbdIntegrationTests : IClassFixture<SabnzbdContainerFixture>, IDisposable
{
    private const string ApiKey = SabnzbdContainerFixture.ApiKey;

    private const string MinimalNzb = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE nzb PUBLIC "-//newzBin//DTD NZB 1.1//EN" "http://www.newzbin.com/DTD/nzb/nzb-1.1.dtd">
        <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
          <file poster="test@example.invalid" date="1234567890" subject="&quot;proxyarr-test.bin&quot; yEnc (1/1)">
            <groups><group>alt.binaries.test</group></groups>
            <segments><segment bytes="1024" number="1">proxyarr-test-segment@example.invalid</segment></segments>
          </file>
        </nzb>
        """;

    private readonly ProxyAppFactory? _factory;
    private readonly HttpClient? _client;

    public SabnzbdIntegrationTests(SabnzbdContainerFixture sabnzbd)
    {
        Assert.SkipWhen(sabnzbd.SkipReason is not null, sabnzbd.SkipReason ?? "");

        _factory = new ProxyAppFactory(
            $"""
            clients:
              sabnzbd:
                upstreams:
                  - name: main
                    url: {sabnzbd.UpstreamUrl}
                instances:
                  - name: sab
                    upstream: main
            """
        );
        _client = _factory.CreateClient();
    }

    private HttpClient Client => _client!;

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Fact]
    public async Task Reports_the_supported_version()
    {
        using var version = await GetJsonAsync("mode=version");

        Assert.Equal("5.0.4", version.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public async Task Config_is_returned()
    {
        using var config = await GetJsonAsync($"mode=get_config&apikey={ApiKey}");

        // Radarr also reads categories out of get_config, but a pristine SABnzbd install has no
        // categories section yet, so only the misc section is guaranteed to be present.
        var misc = config.RootElement.GetProperty("config").GetProperty("misc");
        Assert.Equal(ApiKey, misc.GetProperty("api_key").GetString());
    }

    [Fact]
    public async Task Fullstatus_is_returned()
    {
        using var status = await GetJsonAsync($"mode=fullstatus&skip_dashboard=1&apikey={ApiKey}");

        Assert.True(status.RootElement.GetProperty("status").TryGetProperty("uptime", out _));
    }

    [Fact]
    public async Task Queue_and_history_support_paging_parameters()
    {
        using var queue = await GetJsonAsync($"mode=queue&start=0&limit=50&apikey={ApiKey}");
        Assert.Equal(
            JsonValueKind.Array,
            queue.RootElement.GetProperty("queue").GetProperty("slots").ValueKind
        );

        using var history = await GetJsonAsync($"mode=history&start=0&limit=50&apikey={ApiKey}");
        Assert.Equal(
            JsonValueKind.Array,
            history.RootElement.GetProperty("history").GetProperty("slots").ValueKind
        );
    }

    [Fact]
    public async Task Nzb_upload_and_removal_work_through_the_proxy()
    {
        var ct = TestContext.Current.CancellationToken;

        // Priority -2 ("paused") keeps the job parked in the queue: the container has no Usenet
        // servers configured, so the download must never be attempted.
        using var form = new MultipartFormDataContent();
        var nzbContent = new ByteArrayContent(Encoding.UTF8.GetBytes(MinimalNzb));
        nzbContent.Headers.ContentType = new("application/x-nzb");
        form.Add(nzbContent, "name", "proxyarr-test.nzb");

        var add = await Client.PostAsync(
            $"/sabnzbd/sab/api?mode=addfile&cat=movies&priority=-2&apikey={ApiKey}&output=json",
            form,
            ct
        );
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);

        string nzoId;
        using (var addJson = JsonDocument.Parse(await add.Content.ReadAsStringAsync(ct)))
        {
            Assert.True(addJson.RootElement.GetProperty("status").GetBoolean());
            nzoId = addJson
                .RootElement.GetProperty("nzo_ids")
                .EnumerateArray()
                .Single()
                .GetString()!;
        }

        // The job lands in the queue or (if SABnzbd fails it immediately) in history; Radarr
        // removes from whichever list contains it.
        var source = await WaitForJobAsync(nzoId, ct);

        using var delete = await GetJsonAsync(
            $"mode={source}&name=delete&del_files=1&value={nzoId}&apikey={ApiKey}"
        );
        Assert.True(delete.RootElement.GetProperty("status").GetBoolean());

        Assert.False(
            await FindJobAsync("queue", nzoId, ct) || await FindJobAsync("history", nzoId, ct),
            $"Job {nzoId} is still present after deletion."
        );
    }

    private async Task<string> WaitForJobAsync(string nzoId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (await FindJobAsync("queue", nzoId, ct))
            {
                return "queue";
            }

            if (await FindJobAsync("history", nzoId, ct))
            {
                return "history";
            }

            await Task.Delay(500, ct);
        }

        Assert.Fail($"Job {nzoId} appeared in neither the queue nor the history after 60s.");
        return ""; // unreachable
    }

    private async Task<bool> FindJobAsync(string source, string nzoId, CancellationToken ct)
    {
        var json = await Client.GetStringAsync(
            $"/sabnzbd/sab/api?mode={source}&start=0&limit=100&apikey={ApiKey}&output=json",
            ct
        );

        using var document = JsonDocument.Parse(json);
        return document
            .RootElement.GetProperty(source)
            .GetProperty("slots")
            .EnumerateArray()
            .Any(slot => slot.GetProperty("nzo_id").GetString() == nzoId);
    }

    private async Task<JsonDocument> GetJsonAsync(string query)
    {
        var json = await Client.GetStringAsync(
            $"/sabnzbd/sab/api?{query}&output=json",
            TestContext.Current.CancellationToken
        );
        return JsonDocument.Parse(json);
    }
}
