using System.Text.Json;
using Proxyarr.Tests.Support;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Proxyarr.Tests;

public sealed class PathMappingProxyTests : IDisposable
{
    private readonly WireMockServer _upstream = WireMockServer.Start();
    private readonly ProxyAppFactory _factory;
    private readonly HttpClient _client;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public PathMappingProxyTests()
    {
        _factory = new ProxyAppFactory(
            $"""
            clients:
              qbittorrent:
                upstreams:
                  - name: main
                    url: {_upstream.Url}
                    path_mappings:
                      - from: /downloads/special
                        to: /proxyarr/qbit-special
                      - from: /downloads
                        to: /proxyarr/qbit
                instances:
                  - name: mapped
                    upstream: main
              sabnzbd:
                upstreams:
                  - name: main
                    url: {_upstream.Url}
                    path_mappings:
                      - from: /downloads
                        to: /proxyarr/sab
                instances:
                  - name: mapped
                    upstream: main
            """
        );
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _upstream.Stop();
        _upstream.Dispose();
    }

    [Fact]
    public async Task Qbittorrent_rewrites_every_path_field_consumed_by_arr()
    {
        Stub("/api/v2/app/preferences", """{"save_path":"/downloads"}""");
        Stub(
            "/api/v2/torrents/info",
            """[{"content_path":"/downloads/special/Movie","save_path":"/downloads","name":"Movie"},{"content_path":"/downloads-other/Keep","save_path":"/downloads-other"}]"""
        );
        Stub("/api/v2/torrents/properties", """{"save_path":"/downloads/Movie"}""");
        Stub(
            "/api/v2/torrents/categories",
            """{"movies":{"name":"movies","savePath":"/downloads/movies"}}"""
        );

        using var preferences = await GetJson("/qbittorrent/mapped/api/v2/app/preferences");
        Assert.Equal(
            "/proxyarr/qbit",
            preferences.RootElement.GetProperty("save_path").GetString()
        );

        using var info = await GetJson("/qbittorrent/mapped/api/v2/torrents/info");
        var torrents = info.RootElement.EnumerateArray().ToList();
        Assert.Equal(
            "/proxyarr/qbit-special/Movie",
            torrents[0].GetProperty("content_path").GetString()
        );
        Assert.Equal("/proxyarr/qbit", torrents[0].GetProperty("save_path").GetString());
        Assert.Equal("/downloads-other/Keep", torrents[1].GetProperty("content_path").GetString());

        using var properties = await GetJson(
            "/qbittorrent/mapped/api/v2/torrents/properties?hash=abc"
        );
        Assert.Equal(
            "/proxyarr/qbit/Movie",
            properties.RootElement.GetProperty("save_path").GetString()
        );

        using var categories = await GetJson("/qbittorrent/mapped/api/v2/torrents/categories");
        Assert.Equal(
            "/proxyarr/qbit/movies",
            categories.RootElement.GetProperty("movies").GetProperty("savePath").GetString()
        );
    }

    [Fact]
    public async Task Sabnzbd_rewrites_config_status_and_listing_paths()
    {
        Stub(
            "/api",
            """{"config":{"misc":{"complete_dir":"/downloads/complete"},"categories":[{"name":"relative","dir":"movies"},{"name":"absolute","dir":"/downloads/special"}]}}""",
            "get_config"
        );
        Stub(
            "/api",
            """{"status":{"uptime":"1m","complete_dir":"/downloads/complete","completedir":"/downloads/legacy-complete"}}""",
            "fullstatus"
        );
        Stub(
            "/api",
            """{"history":{"slots":[{"nzo_id":"one","storage":"/downloads/complete/Movie"}]}}""",
            "history"
        );
        Stub(
            "/api",
            """{"queue":{"slots":[{"nzo_id":"two","storage":"/downloads/incomplete/Movie"}]}}""",
            "queue"
        );

        using var config = await GetJson("/sabnzbd/mapped/api?mode=get_config&output=json");
        var configRoot = config.RootElement.GetProperty("config");
        Assert.Equal(
            "/proxyarr/sab/complete",
            configRoot.GetProperty("misc").GetProperty("complete_dir").GetString()
        );
        var categoryDirs = configRoot
            .GetProperty("categories")
            .EnumerateArray()
            .Select(category => category.GetProperty("dir").GetString())
            .ToList();
        Assert.Contains("movies", categoryDirs);
        Assert.Contains("/proxyarr/sab/special", categoryDirs);

        using var status = await GetJson("/sabnzbd/mapped/api?mode=fullstatus&output=json");
        Assert.Equal(
            "/proxyarr/sab/complete",
            status.RootElement.GetProperty("status").GetProperty("complete_dir").GetString()
        );
        Assert.Equal(
            "/proxyarr/sab/legacy-complete",
            status.RootElement.GetProperty("status").GetProperty("completedir").GetString()
        );

        using var history = await GetJson("/sabnzbd/mapped/api?mode=history&output=json");
        Assert.Equal(
            "/proxyarr/sab/complete/Movie",
            Slot(history, "history").GetProperty("storage").GetString()
        );

        using var queue = await GetJson("/sabnzbd/mapped/api?mode=queue&output=json");
        Assert.Equal(
            "/proxyarr/sab/incomplete/Movie",
            Slot(queue, "queue").GetProperty("storage").GetString()
        );
    }

    [Fact]
    public async Task Rewritten_requests_disable_upstream_compression()
    {
        Stub("/api/v2/torrents/info", "[]");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/qbittorrent/mapped/api/v2/torrents/info"
        );
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");

        await _client.SendAsync(request, Ct);

        var received = _upstream.LogEntries.Last().RequestMessage!;
        Assert.False(received.Headers!.ContainsKey("Accept-Encoding"));
    }

    private async Task<JsonDocument> GetJson(string path)
    {
        var json = await _client.GetStringAsync(path, Ct);
        return JsonDocument.Parse(json);
    }

    private void Stub(string path, string body, string? mode = null)
    {
        var request = Request.Create().WithPath(path).UsingGet();
        if (mode is not null)
        {
            request.WithParam("mode", mode);
        }

        _upstream
            .Given(request)
            .RespondWith(
                Response
                    .Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(body)
            );
    }

    private static JsonElement Slot(JsonDocument document, string container) =>
        document.RootElement.GetProperty(container).GetProperty("slots").EnumerateArray().Single();
}
