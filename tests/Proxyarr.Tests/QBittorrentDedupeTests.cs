using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Proxyarr.Tests.Support;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Proxyarr.Tests;

/// <summary>
/// Unit coverage for qBittorrent cross-instance dedup, driving the real proxy against a WireMock
/// fake qBittorrent. Two instances (radarr1/radarr2) reference the same named dedupe group — the
/// same shape as two *arrs grabbing the same release.
/// </summary>
public sealed class QBittorrentDedupeTests : IDisposable
{
    private const string InfoPath = "/api/v2/torrents/info";
    private const string AddPath = "/api/v2/torrents/add";
    private const string AddTagsPath = "/api/v2/torrents/addTags";
    private const string RemoveTagsPath = "/api/v2/torrents/removeTags";
    private const string SetShareLimitsPath = "/api/v2/torrents/setShareLimits";
    private const string DeletePath = "/api/v2/torrents/delete";
    private const string CreateCategoryPath = "/api/v2/torrents/createCategory";
    private const string CategoriesPath = "/api/v2/torrents/categories";
    private const string PreferencesPath = "/api/v2/app/preferences";

    private readonly WireMockServer _upstream = WireMockServer.Start();
    private ProxyAppFactory? _factory;
    private HttpClient? _client;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
        _upstream.Stop();
        _upstream.Dispose();
    }

    private HttpClient Boot(
        string? category = "proxyarr",
        bool withPlainInstance = false,
        bool withPathMapping = false
    )
    {
        var plain = withPlainInstance
            ? """

                      - name: plain
                        upstream: main
                """
            : "";
        var pathMapping = withPathMapping
            ? """

                        path_mappings:
                          - from: /downloads
                            to: /proxyarr/qbit
                """
            : "";

        _factory = new ProxyAppFactory(
            $"""
            clients:
              qbittorrent:
                upstreams:
                  - name: main
                    url: {_upstream.Url}{pathMapping}
                groups:
                  - name: shared
                    category: {category}
                instances:
                  - name: radarr1
                    upstream: main
                    group: shared
                  - name: radarr2
                    upstream: main
                    group: shared{plain}
            """
        );
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        return _client;
    }

    // ---- add ------------------------------------------------------------------------------------

    [Fact]
    public async Task Add_new_rewrites_the_form_and_pins_the_share_limit_action_to_stop()
    {
        var client = Boot();
        var (torrent, _) = TestTorrent.Create("proxyarr-movie");
        StubInfo("[]");
        StubPost(AddPath, "Ok.");
        StubPost(SetShareLimitsPath);

        var response = await AddTorrent(
            client,
            "radarr1",
            torrent,
            ("category", "movies"),
            ("stopped", "true"),
            ("paused", "true")
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Ok.", await response.Content.ReadAsStringAsync(Ct));

        var addBody = Single(AddPath);
        Assert.Contains("tags", addBody);
        Assert.Contains("radarr1", addBody); // instance tag injected
        Assert.Contains("proxyarr", addBody); // configured group category
        Assert.DoesNotContain("movies", addBody); // the *arr's category is never forwarded
        Assert.Contains("stopped", addBody);
        Assert.Contains("paused", addBody);

        var shareLimits = Single(SetShareLimitsPath);
        Assert.Contains("shareLimitAction=Stop", shareLimits); // Stop
    }

    [Fact]
    public async Task Add_new_without_a_configured_category_drops_the_category_entirely()
    {
        var client = Boot(category: null);
        var (torrent, _) = TestTorrent.Create("proxyarr-nocat");
        StubInfo("[]");
        StubPost(AddPath, "Ok.");
        StubPost(SetShareLimitsPath);

        await AddTorrent(client, "radarr1", torrent, ("category", "movies"));

        var addBody = Single(AddPath);
        Assert.DoesNotContain("category", addBody); // no category field forwarded at all
        Assert.DoesNotContain("movies", addBody);
        Assert.Contains("radarr1", addBody); // tag still injected
    }

    [Fact]
    public async Task Add_existing_tags_instead_of_re_adding()
    {
        var client = Boot();
        var (torrent, hash) = TestTorrent.Create("proxyarr-dup");
        StubInfo(TorrentArray(hash, tags: "radarr1"));
        StubPost(AddTagsPath);
        StubPost(SetShareLimitsPath);

        var response = await AddTorrent(client, "radarr2", torrent, ("category", "movies"));

        Assert.Equal("Ok.", await response.Content.ReadAsStringAsync(Ct));
        Assert.Empty(Entries(AddPath)); // never re-added
        Assert.Contains("radarr2", Single(AddTagsPath));
    }

    [Fact]
    public async Task Add_existing_does_not_touch_share_limits_when_nothing_is_raised()
    {
        var client = Boot();
        var (torrent, hash) = TestTorrent.Create("proxyarr-noraise");
        StubInfo(TorrentArray(hash, tags: "radarr1", ratioLimit: "2"));
        StubPost(AddTagsPath);
        StubPost(SetShareLimitsPath);

        await AddTorrent(client, "radarr2", torrent); // no limit fields → nothing to raise

        Assert.Empty(Entries(SetShareLimitsPath));
    }

    [Fact]
    public async Task Add_existing_raises_share_limits_to_the_max()
    {
        var client = Boot();
        var (torrent, hash) = TestTorrent.Create("proxyarr-raise");
        StubInfo(TorrentArray(hash, tags: "radarr1", ratioLimit: "1"));
        StubPost(AddTagsPath);
        StubPost(SetShareLimitsPath);

        await AddTorrent(client, "radarr2", torrent, ("ratioLimit", "3"));

        var shareLimits = Single(SetShareLimitsPath);
        Assert.Contains("ratioLimit=3", shareLimits);
        Assert.Contains("shareLimitAction=Stop", shareLimits);
    }

    [Fact]
    public async Task Concurrent_adds_of_the_same_release_add_once_and_tag_once()
    {
        var client = Boot();
        var (torrent, hash) = TestTorrent.Create("proxyarr-race");

        // info returns empty until the add flips the scenario; the add is delayed so that, without
        // the keyed lock, both requests would observe "empty" and both would POST /add.
        _upstream
            .Given(Request.Create().WithPath(InfoPath).UsingGet())
            .InScenario("race")
            .RespondWith(Json("[]"));
        _upstream
            .Given(Request.Create().WithPath(AddPath).UsingPost())
            .InScenario("race")
            .WillSetStateTo("added")
            .RespondWith(
                Response.Create().WithBody("Ok.").WithDelay(TimeSpan.FromMilliseconds(300))
            );
        _upstream
            .Given(Request.Create().WithPath(InfoPath).UsingGet())
            .InScenario("race")
            .WhenStateIs("added")
            .RespondWith(Json(TorrentArray(hash, tags: "radarr1")));
        StubPost(AddTagsPath);
        StubPost(SetShareLimitsPath);

        var (bytes1, _) = (torrent, hash);
        var add1 = AddTorrent(client, "radarr1", bytes1);
        var add2 = AddTorrent(client, "radarr2", bytes1);
        await Task.WhenAll(add1, add2);

        Assert.Single(Entries(AddPath));
        Assert.Single(Entries(AddTagsPath));
    }

    // ---- info -----------------------------------------------------------------------------------

    [Fact]
    public async Task Info_request_without_category_still_filters_by_instance_tag()
    {
        var client = Boot();
        _upstream
            .Given(Request.Create().WithPath(InfoPath).UsingGet())
            .RespondWith(Json("""[{"hash":"abc","tags":"radarr1","category":"proxyarr"}]"""));

        var response = await client.GetAsync(
            "/qbittorrent/radarr1/api/v2/torrents/info?hashes=abc",
            Ct
        );

        var received = Single(_upstream.LogEntries.Last());
        Assert.Equal("radarr1", received.Query!["tag"].Single());
        Assert.Equal("abc", received.Query!["hashes"].Single());
        Assert.False(received.Query!.ContainsKey("category"));

        var body = await response.Content.ReadAsStringAsync(Ct);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(
            "proxyarr",
            json.RootElement.EnumerateArray().Single().GetProperty("category").GetString()
        );
    }

    [Fact]
    public async Task Info_request_rewrites_category_to_tag_and_strips_accept_encoding()
    {
        var client = Boot();
        _upstream
            .Given(Request.Create().WithPath(InfoPath).UsingGet())
            .RespondWith(Json(TorrentArray("abc", tags: "radarr1")));

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/qbittorrent/radarr1/api/v2/torrents/info?category=movies"
        );
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
        var response = await client.SendAsync(request, Ct);

        var received = Single(_upstream.LogEntries.Last());
        Assert.Equal("radarr1", received.Query!["tag"].Single());
        Assert.False(received.Query!.ContainsKey("category"));
        Assert.False(received.Headers!.ContainsKey("Accept-Encoding"));

        var body = await response.Content.ReadAsStringAsync(Ct);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(
            "movies",
            json.RootElement.EnumerateArray().Single().GetProperty("category").GetString()
        );
    }

    [Fact]
    public async Task Info_response_composes_category_and_path_rewriting()
    {
        var client = Boot(withPathMapping: true);
        _upstream
            .Given(Request.Create().WithPath(InfoPath).UsingGet())
            .RespondWith(
                Json(
                    """[{"hash":"abc","tags":"radarr1","category":"proxyarr","content_path":"/downloads/Movie","save_path":"/downloads"}]"""
                )
            );

        var body = await client.GetStringAsync(
            "/qbittorrent/radarr1/api/v2/torrents/info?category=movies",
            Ct
        );
        using var json = JsonDocument.Parse(body);
        var torrent = json.RootElement.EnumerateArray().Single();
        Assert.Equal("movies", torrent.GetProperty("category").GetString());
        Assert.Equal("/proxyarr/qbit/Movie", torrent.GetProperty("content_path").GetString());
        Assert.Equal("/proxyarr/qbit", torrent.GetProperty("save_path").GetString());
    }

    // ---- categories -----------------------------------------------------------------------------

    [Fact]
    public async Task Categories_response_injects_remembered_categories()
    {
        var client = Boot();
        StubPost(CreateCategoryPath, "Ok.");
        _upstream
            .Given(Request.Create().WithPath(CategoriesPath).UsingGet())
            .RespondWith(Json("{}"));

        // Radarr creates its category first; the proxy remembers it locally.
        await client.PostAsync(
            "/qbittorrent/radarr1/api/v2/torrents/createCategory",
            Form(("category", "movies")),
            Ct
        );

        var categories = await client.GetStringAsync(
            "/qbittorrent/radarr1/api/v2/torrents/categories",
            Ct
        );
        using var json = JsonDocument.Parse(categories);
        Assert.True(json.RootElement.TryGetProperty("movies", out var movies));
        Assert.Equal("movies", movies.GetProperty("name").GetString());
    }

    // ---- delete ---------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_with_a_sibling_tag_remaining_only_removes_the_tag()
    {
        var client = Boot();
        StubInfo(TorrentArray("h1", tags: "radarr1,radarr2"));
        StubPost(RemoveTagsPath);
        StubPost(DeletePath);

        await client.PostAsync(
            "/qbittorrent/radarr1/api/v2/torrents/delete",
            Form(("hashes", "h1"), ("deleteFiles", "true")),
            Ct
        );

        Assert.Contains("radarr1", Single(RemoveTagsPath));
        Assert.Empty(Entries(DeletePath)); // no upstream delete while a sibling still owns it
    }

    [Fact]
    public async Task Last_delete_below_the_seed_limit_hands_cleanup_to_qbittorrent()
    {
        var client = Boot();
        StubInfo(
            TorrentArray(
                "h1",
                tags: "radarr1",
                ratio: "0.1",
                ratioLimit: "2",
                seedingTimeLimit: "-1"
            )
        );
        StubPost(RemoveTagsPath);
        StubPost(SetShareLimitsPath);
        StubPost(DeletePath);

        await client.PostAsync(
            "/qbittorrent/radarr1/api/v2/torrents/delete",
            Form(("hashes", "h1"), ("deleteFiles", "true")),
            Ct
        );

        Assert.Contains("radarr1", Single(RemoveTagsPath));
        Assert.Empty(Entries(DeletePath)); // never an immediate delete
        var shareLimits = Single(SetShareLimitsPath);
        Assert.Contains("shareLimitAction=RemoveWithContent", shareLimits); // RemoveWithContent
        Assert.Contains("ratioLimit=2", shareLimits); // current limits preserved
    }

    [Fact]
    public async Task Last_delete_past_the_seed_limit_removes_with_files_now()
    {
        var client = Boot();
        StubInfo(
            TorrentArray("h1", tags: "radarr1", ratio: "3", ratioLimit: "2", seedingTimeLimit: "-1")
        );
        StubPost(RemoveTagsPath);
        StubPost(DeletePath);

        await client.PostAsync(
            "/qbittorrent/radarr1/api/v2/torrents/delete",
            Form(("hashes", "h1"), ("deleteFiles", "false")),
            Ct
        );

        Assert.Contains("radarr1", Single(RemoveTagsPath));
        var delete = Single(DeletePath);
        Assert.Contains("deleteFiles=true", delete); // request's deleteFiles ignored under dedupe
    }

    [Fact]
    public async Task Last_delete_resolves_global_limits_from_preferences()
    {
        var client = Boot();
        StubInfo(TorrentArray("h1", tags: "radarr1", ratio: "3", ratioLimit: "-2"));
        _upstream
            .Given(Request.Create().WithPath(PreferencesPath).UsingGet())
            .RespondWith(
                Json(
                    """{"max_ratio_enabled":true,"max_ratio":2.0,"max_seeding_time_enabled":false,"max_seeding_time":-1}"""
                )
            );
        StubPost(RemoveTagsPath);
        StubPost(DeletePath);

        await client.PostAsync(
            "/qbittorrent/radarr1/api/v2/torrents/delete",
            Form(("hashes", "h1")),
            Ct
        );

        Assert.NotEmpty(Entries(PreferencesPath));
        Assert.Contains("deleteFiles=true", Single(DeletePath));
    }

    // ---- setShareLimits -------------------------------------------------------------------------

    [Fact]
    public async Task SetShareLimits_max_merges_and_pins_stop()
    {
        var client = Boot();
        StubInfo(
            TorrentArray(
                "h1",
                tags: "radarr1",
                ratioLimit: "1",
                seedingTimeLimit: "60",
                inactive: "-2"
            )
        );
        StubPost(SetShareLimitsPath);

        await client.PostAsync(
            "/qbittorrent/radarr1/api/v2/torrents/setShareLimits",
            Form(
                ("hashes", "h1"),
                ("ratioLimit", "2"),
                ("seedingTimeLimit", "30"),
                ("inactiveSeedingTimeLimit", "-1"),
                ("shareLimitAction", "-1")
            ),
            Ct
        );

        var body = Single(SetShareLimitsPath);
        Assert.Contains("ratioLimit=2", body); // max(1, 2)
        Assert.Contains("seedingTimeLimit=60", body); // max(60, 30)
        Assert.Contains("inactiveSeedingTimeLimit=-1", body); // unlimited wins
        Assert.Contains("shareLimitAction=Stop", body); // Stop pinned, overriding the -1 sent
    }

    // ---- setCategory ----------------------------------------------------------------------------

    [Fact]
    public async Task SetCategory_swaps_the_instance_tag_for_the_category_tag()
    {
        var client = Boot();
        StubPost(RemoveTagsPath);
        StubPost(AddTagsPath);

        await client.PostAsync(
            "/qbittorrent/radarr1/api/v2/torrents/setCategory",
            Form(("hashes", "h1"), ("category", "imported")),
            Ct
        );

        Assert.Contains("radarr1", Single(RemoveTagsPath));
        Assert.Contains("imported", Single(AddTagsPath));
    }

    // ---- error propagation ----------------------------------------------------------------------

    [Fact]
    public async Task Side_call_403_propagates_so_radarr_relogins()
    {
        var client = Boot();
        _upstream
            .Given(Request.Create().WithPath(InfoPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(403).WithBody("Forbidden"));

        var response = await client.PostAsync(
            "/qbittorrent/radarr1/api/v2/torrents/delete",
            Form(("hashes", "h1")),
            Ct
        );

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- regression: dedupe-off stays byte-identical --------------------------------------------

    [Fact]
    public async Task Dedupe_off_instance_is_an_unchanged_pass_through()
    {
        var client = Boot(withPlainInstance: true);
        StubPost(AddPath, "Ok.");

        var (torrent, _) = TestTorrent.Create("plain-movie");
        await AddTorrent(client, "plain", torrent, ("category", "movies"), ("tags", "keep"));

        var addBody = Single(AddPath);
        Assert.Contains("movies", addBody); // category forwarded verbatim
        Assert.DoesNotContain("radarr", addBody); // no instance tag injected
        Assert.Empty(Entries(AddTagsPath));
        Assert.Empty(Entries(SetShareLimitsPath));
        Assert.Empty(Entries(InfoPath)); // no dedup side-calls
    }

    // ---- helpers --------------------------------------------------------------------------------

    private async Task<HttpResponseMessage> AddTorrent(
        HttpClient client,
        string prefix,
        byte[] torrentBytes,
        params (string Key, string Value)[] fields
    )
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(torrentBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/x-bittorrent");
        form.Add(file, "torrents", "test.torrent");
        foreach (var (key, value) in fields)
        {
            form.Add(new StringContent(value), key);
        }

        return await client.PostAsync($"/qbittorrent/{prefix}/api/v2/torrents/add", form, Ct);
    }

    private void StubInfo(string body) =>
        _upstream.Given(Request.Create().WithPath(InfoPath).UsingGet()).RespondWith(Json(body));

    private void StubPost(string path, string body = "") =>
        _upstream
            .Given(Request.Create().WithPath(path).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(body));

    private static IResponseBuilder Json(string body) =>
        Response
            .Create()
            .WithStatusCode(200)
            .WithHeader("Content-Type", "application/json")
            .WithBody(body);

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value)));

    private static string TorrentArray(
        string hash,
        string tags,
        string ratio = "0",
        string seedingTime = "0",
        string ratioLimit = "-2",
        string seedingTimeLimit = "-2",
        string inactive = "-2"
    ) =>
        $$"""
            [{"hash":"{{hash}}","category":"proxyarr","tags":"{{tags}}","ratio":{{ratio}},"seeding_time":{{seedingTime}},"ratio_limit":{{ratioLimit}},"seeding_time_limit":{{seedingTimeLimit}},"inactive_seeding_time_limit":{{inactive}}}]
            """;

    private IReadOnlyList<WireMock.Logging.ILogEntry> Entries(string path) =>
        _upstream.LogEntries.Where(entry => entry.RequestMessage!.Path == path).ToList();

    // WireMock keeps a multipart (binary) body in BodyAsBytes rather than Body; Latin1 round-trips
    // every byte so ASCII field markers stay findable.
    private string Single(string path)
    {
        var message = Entries(path).Single().RequestMessage!;
        return message.Body
            ?? (
                message.BodyAsBytes is { } bytes ? System.Text.Encoding.Latin1.GetString(bytes) : ""
            );
    }

    private static WireMock.IRequestMessage Single(WireMock.Logging.ILogEntry entry) =>
        entry.RequestMessage!;
}
