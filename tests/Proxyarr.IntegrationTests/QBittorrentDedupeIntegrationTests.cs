using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Proxyarr.IntegrationTests.Support;

namespace Proxyarr.IntegrationTests;

/// <summary>
/// Exercises qBittorrent cross-instance dedup against a real qBittorrent 5.2.3 container. Two dedup
/// groups share the one container: <c>main</c> (radarr1/radarr2, category <c>proxyarr-it</c>) and
/// <c>nocat</c> (sonarr1/sonarr2, no category), separated by explicit group overrides.
/// </summary>
public sealed class QBittorrentDedupeIntegrationTests
    : IClassFixture<QBittorrentContainerFixture>,
        IDisposable
{
    private const string Category = "proxyarr-it";

    private readonly string _upstreamUrl;
    private readonly ProxyAppFactory? _factory;
    private readonly HttpClient? _client;
    private readonly HttpClient _raw = new();

    public QBittorrentDedupeIntegrationTests(QBittorrentContainerFixture qbittorrent)
    {
        Assert.SkipWhen(qbittorrent.SkipReason is not null, qbittorrent.SkipReason ?? "");
        _upstreamUrl = qbittorrent.UpstreamUrl;

        _factory = new ProxyAppFactory(
            $"""
            clients:
              qbittorrent:
                upstreams:
                  - name: main
                    url: {_upstreamUrl}
                groups:
                  - name: main
                    category: {Category}
                  - name: nocat
                instances:
                  - name: radarr1
                    upstream: main
                    group: main
                  - name: radarr2
                    upstream: main
                    group: main
                  - name: sonarr1
                    upstream: main
                    group: nocat
                  - name: sonarr2
                    upstream: main
                    group: nocat
            """
        );
        _client = _factory.CreateClient();
    }

    private HttpClient Client => _client!;

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
        _raw.Dispose();
    }

    [Fact]
    public async Task Duplicate_grab_is_shared_as_one_torrent_with_both_tags()
    {
        var ct = TestContext.Current.CancellationToken;
        var (torrent, hash) = TestTorrent.Create($"dup-{Guid.NewGuid():N}");

        // Radarr creates its category up front; the proxy ensures the real category exists upstream.
        await CreateCategory("radarr1", "movies", ct);

        await AddAsync("radarr1", torrent, ct, ("category", "movies"), ("ratioLimit", "1"));
        await AddAsync("radarr2", torrent, ct, ("category", "tv"), ("ratioLimit", "3"));

        var info = await WaitForRawAsync(hash, present: true, ct);
        Assert.Equal(Category, info.GetProperty("category").GetString());
        Assert.Equal(new[] { "radarr1", "radarr2" }, Tags(info).Order());
        Assert.Equal(3, info.GetProperty("ratio_limit").GetDouble()); // max of the two
        Assert.Equal("Stop", info.GetProperty("share_limit_action").GetString());

        // Each instance sees the torrent under the category it sent.
        Assert.Equal("movies", await ProxyCategory("radarr1", "movies", hash, ct));
        Assert.Equal("tv", await ProxyCategory("radarr2", "tv", hash, ct));
    }

    [Fact]
    public async Task A_group_without_a_configured_category_lands_the_torrent_uncategorized()
    {
        var ct = TestContext.Current.CancellationToken;
        var (torrent, hash) = TestTorrent.Create($"nocat-{Guid.NewGuid():N}");

        await AddAsync("sonarr1", torrent, ct, ("category", "shows"));

        var info = await WaitForRawAsync(hash, present: true, ct);
        Assert.Equal("", info.GetProperty("category").GetString());
        Assert.Equal(["sonarr1"], Tags(info));
    }

    [Fact]
    public async Task Delete_keeps_the_torrent_until_the_last_owner_leaves()
    {
        var ct = TestContext.Current.CancellationToken;
        var (torrent, hash) = TestTorrent.Create($"del-{Guid.NewGuid():N}");

        // High ratio limit: the torrent can never reach it (random piece never seeds).
        await AddAsync("radarr1", torrent, ct, ("category", "movies"), ("ratioLimit", "100"));
        await AddAsync("radarr2", torrent, ct, ("category", "tv"), ("ratioLimit", "100"));
        await WaitForRawAsync(hash, present: true, ct);

        // First delete: torrent survives, only radarr1's tag is gone, no files touched.
        await DeleteAsync("radarr1", hash, deleteFiles: true, ct);
        var afterFirst = await WaitForRawAsync(hash, present: true, ct);
        Assert.Equal(["radarr2"], Tags(afterFirst));

        // Last delete (limit unreachable): still present, no managed tags, cleanup handed to qBt.
        await DeleteAsync("radarr2", hash, deleteFiles: true, ct);
        var afterLast = await WaitForRawAsync(hash, present: true, ct);
        Assert.Empty(Tags(afterLast));
        Assert.Equal("RemoveWithContent", afterLast.GetProperty("share_limit_action").GetString());

        // Clean up the lingering torrent so it can't affect other runs.
        await _raw.PostAsync(
            $"{_upstreamUrl}/api/v2/torrents/delete",
            Form(("hashes", hash), ("deleteFiles", "true")),
            ct
        );
    }

    [Fact]
    public async Task Last_delete_of_an_already_surpassed_torrent_removes_it_now()
    {
        var ct = TestContext.Current.CancellationToken;
        var (torrent, hash) = TestTorrent.Create($"gone-{Guid.NewGuid():N}");

        // ratioLimit 0 is surpassed the instant the torrent exists (ratio 0 >= 0).
        await AddAsync("radarr1", torrent, ct, ("category", "movies"), ("ratioLimit", "0"));
        await WaitForRawAsync(hash, present: true, ct);

        await DeleteAsync("radarr1", hash, deleteFiles: false, ct);

        await WaitForRawAsync(hash, present: false, ct);
    }

    [Fact]
    public async Task Concurrent_double_add_yields_a_single_torrent()
    {
        var ct = TestContext.Current.CancellationToken;
        var (torrent, hash) = TestTorrent.Create($"conc-{Guid.NewGuid():N}");

        await Task.WhenAll(
            AddAsync("radarr1", torrent, ct, ("category", "movies")),
            AddAsync("radarr2", torrent, ct, ("category", "tv"))
        );

        var info = await WaitForRawAsync(hash, present: true, ct);
        Assert.Equal(new[] { "radarr1", "radarr2" }, Tags(info).Order());
        Assert.Single(await RawInfoAsync(hash, ct));
    }

    // ---- helpers --------------------------------------------------------------------------------

    private async Task AddAsync(
        string prefix,
        byte[] torrent,
        CancellationToken ct,
        params (string Key, string Value)[] fields
    )
    {
        var content = new ByteArrayContent(torrent);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-bittorrent");
        using var form = new MultipartFormDataContent { { content, "torrents", "test.torrent" } };
        form.Add(new StringContent("true"), "stopped");
        form.Add(new StringContent("true"), "paused");
        foreach (var (key, value) in fields)
        {
            form.Add(new StringContent(value), key);
        }

        var response = await Client.PostAsync(
            $"/qbittorrent/{prefix}/api/v2/torrents/add",
            form,
            ct
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task CreateCategory(string prefix, string category, CancellationToken ct)
    {
        var response = await Client.PostAsync(
            $"/qbittorrent/{prefix}/api/v2/torrents/createCategory",
            Form(("category", category)),
            ct
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task DeleteAsync(
        string prefix,
        string hash,
        bool deleteFiles,
        CancellationToken ct
    )
    {
        var response = await Client.PostAsync(
            $"/qbittorrent/{prefix}/api/v2/torrents/delete",
            Form(("hashes", hash), ("deleteFiles", deleteFiles ? "true" : "false")),
            ct
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<string?> ProxyCategory(
        string prefix,
        string category,
        string hash,
        CancellationToken ct
    )
    {
        var json = await Client.GetStringAsync(
            $"/qbittorrent/{prefix}/api/v2/torrents/info?category={category}",
            ct
        );
        using var doc = JsonDocument.Parse(json);
        return doc
            .RootElement.EnumerateArray()
            .FirstOrDefault(t =>
                string.Equals(
                    t.GetProperty("hash").GetString(),
                    hash,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .GetProperty("category")
            .GetString();
    }

    private async Task<List<JsonElement>> RawInfoAsync(string hash, CancellationToken ct)
    {
        var json = await _raw.GetStringAsync(
            $"{_upstreamUrl}/api/v2/torrents/info?hashes={hash}",
            ct
        );
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private async Task<JsonElement> WaitForRawAsync(string hash, bool present, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var torrents = await RawInfoAsync(hash, ct);
            if ((torrents.Count > 0) == present)
            {
                return torrents.Count > 0 ? torrents[0] : default;
            }

            await Task.Delay(300, ct);
        }

        Assert.Fail($"Torrent {hash} was {(present ? "not present" : "still present")} after 30s.");
        return default;
    }

    private static string[] Tags(JsonElement info) =>
        info.GetProperty("tags")
            .GetString()!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value)));
}
