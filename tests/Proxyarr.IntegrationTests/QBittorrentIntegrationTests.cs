using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Proxyarr.IntegrationTests.Support;

namespace Proxyarr.IntegrationTests;

/// <summary>
/// Exercises every proxied qBittorrent endpoint against a real qBittorrent 5.2.3 container,
/// mirroring the call patterns of Radarr's download client.
/// </summary>
public sealed class QBittorrentIntegrationTests
    : IClassFixture<QBittorrentContainerFixture>,
        IDisposable
{
    private readonly ProxyAppFactory? _factory;
    private readonly HttpClient? _client;

    public QBittorrentIntegrationTests(QBittorrentContainerFixture qbittorrent)
    {
        Assert.SkipWhen(qbittorrent.SkipReason is not null, qbittorrent.SkipReason ?? "");

        _factory = new ProxyAppFactory(
            $"""
            clients:
              qbittorrent:
                upstreams:
                  - name: main
                    url: {qbittorrent.UpstreamUrl}
                instances:
                  - name: qbit
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
    public async Task Login_succeeds_through_the_proxy()
    {
        var response = await Client.PostAsync(
            "/qbittorrent/qbit/api/v2/auth/login",
            new FormUrlEncodedContent(
                new Dictionary<string, string> { ["username"] = "admin", ["password"] = "any" }
            ),
            TestContext.Current.CancellationToken
        );

        // qBittorrent 5.2.x answers 200 "Ok." on a fresh login and 204 when the session is
        // already authenticated (the fixture whitelists all subnets).
        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"login returned {(int)response.StatusCode}"
        );
    }

    [Fact]
    public async Task Reports_the_supported_api_and_app_versions()
    {
        var apiVersion = await Client.GetStringAsync(
            "/qbittorrent/qbit/api/v2/app/webapiVersion",
            TestContext.Current.CancellationToken
        );
        var appVersion = await Client.GetStringAsync(
            "/qbittorrent/qbit/api/v2/app/version",
            TestContext.Current.CancellationToken
        );

        // qBittorrent 5.2.3 ships Web API 2.15.x; Radarr uses this value to pick its code paths
        // (>= 2.11.0 selects the stop/start naming introduced by qBittorrent 5.0).
        Assert.StartsWith("2.15", apiVersion);
        Assert.StartsWith("v5.2.3", appVersion);
    }

    [Fact]
    public async Task Preferences_are_returned_as_json()
    {
        var json = await Client.GetStringAsync(
            "/qbittorrent/qbit/api/v2/app/preferences",
            TestContext.Current.CancellationToken
        );

        using var preferences = JsonDocument.Parse(json);
        Assert.True(preferences.RootElement.TryGetProperty("save_path", out _));
    }

    [Fact]
    public async Task Full_torrent_lifecycle_works_through_the_proxy()
    {
        var ct = TestContext.Current.CancellationToken;
        const string category = "proxyarr-test";

        // Radarr creates its category up front.
        var createCategory = await Client.PostAsync(
            "/qbittorrent/qbit/api/v2/torrents/createCategory",
            Form(("category", category)),
            ct
        );
        Assert.True(
            createCategory.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
            $"createCategory returned {(int)createCategory.StatusCode}"
        );

        var categories = await Client.GetStringAsync(
            "/qbittorrent/qbit/api/v2/torrents/categories",
            ct
        );
        Assert.Contains(category, categories);

        // Add a stopped torrent, the same way Radarr uploads a .torrent file.
        var (torrentBytes, infoHash) = TestTorrent.Create($"proxyarr-{Guid.NewGuid():N}");
        var torrentContent = new ByteArrayContent(torrentBytes);
        torrentContent.Headers.ContentType = new MediaTypeHeaderValue("application/x-bittorrent");
        using var addForm = new MultipartFormDataContent();
        addForm.Add(torrentContent, "torrents", "test.torrent");
        addForm.Add(new StringContent(category), "category");
        addForm.Add(new StringContent("true"), "stopped");
        addForm.Add(new StringContent("true"), "paused");

        var add = await Client.PostAsync("/qbittorrent/qbit/api/v2/torrents/add", addForm, ct);
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        // Web API 2.15 returns {"added_torrent_ids": [...]}; older versions returned "Ok.".
        // The poll below is the real success check either way.
        Assert.NotEqual("Fails.", await add.Content.ReadAsStringAsync(ct));

        await WaitForTorrentAsync(infoHash, expectPresent: true, ct);

        var properties = await Client.GetStringAsync(
            $"/qbittorrent/qbit/api/v2/torrents/properties?hash={infoHash}",
            ct
        );
        using (var propertiesJson = JsonDocument.Parse(properties))
        {
            Assert.True(propertiesJson.RootElement.TryGetProperty("save_path", out _));
        }

        var files = await Client.GetStringAsync(
            $"/qbittorrent/qbit/api/v2/torrents/files?hash={infoHash}",
            ct
        );
        using (var filesJson = JsonDocument.Parse(files))
        {
            Assert.Single(filesJson.RootElement.EnumerateArray());
        }

        // Web API 2.15 requires all four parameters (shareLimitAction -1 = "default"); a
        // Radarr-style call omitting them gets a 400, which the proxy passes through as-is.
        var setShareLimits = await Client.PostAsync(
            "/qbittorrent/qbit/api/v2/torrents/setShareLimits",
            Form(
                ("hashes", infoHash),
                ("ratioLimit", "1.5"),
                ("seedingTimeLimit", "60"),
                ("inactiveSeedingTimeLimit", "-2"),
                ("shareLimitAction", "-1")
            ),
            ct
        );
        Assert.Equal(HttpStatusCode.OK, setShareLimits.StatusCode);

        var setForceStart = await Client.PostAsync(
            "/qbittorrent/qbit/api/v2/torrents/setForceStart",
            Form(("hashes", infoHash), ("value", "false")),
            ct
        );
        Assert.Equal(HttpStatusCode.OK, setForceStart.StatusCode);

        // 409 is expected when torrent queueing is disabled (the qBittorrent default); Radarr
        // treats both outcomes as success.
        var topPrio = await Client.PostAsync(
            "/qbittorrent/qbit/api/v2/torrents/topPrio",
            Form(("hashes", infoHash)),
            ct
        );
        Assert.True(
            topPrio.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
            $"topPrio returned {(int)topPrio.StatusCode}"
        );

        var setCategory = await Client.PostAsync(
            "/qbittorrent/qbit/api/v2/torrents/setCategory",
            Form(("hashes", infoHash), ("category", category)),
            ct
        );
        Assert.Equal(HttpStatusCode.OK, setCategory.StatusCode);

        var delete = await Client.PostAsync(
            "/qbittorrent/qbit/api/v2/torrents/delete",
            Form(("hashes", infoHash), ("deleteFiles", "true")),
            ct
        );
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        await WaitForTorrentAsync(infoHash, expectPresent: false, ct);
    }

    private async Task WaitForTorrentAsync(
        string infoHash,
        bool expectPresent,
        CancellationToken ct
    )
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var json = await Client.GetStringAsync("/qbittorrent/qbit/api/v2/torrents/info", ct);
            using var torrents = JsonDocument.Parse(json);
            var present = torrents
                .RootElement.EnumerateArray()
                .Any(torrent =>
                    string.Equals(
                        torrent.GetProperty("hash").GetString(),
                        infoHash,
                        StringComparison.OrdinalIgnoreCase
                    )
                );

            if (present == expectPresent)
            {
                return;
            }

            await Task.Delay(500, ct);
        }

        Assert.Fail(
            $"Torrent {infoHash} was {(expectPresent ? "not found" : "still present")} after 60s."
        );
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value)));
}
