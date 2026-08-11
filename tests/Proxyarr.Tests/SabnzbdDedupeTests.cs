using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Proxyarr.Tests.Support;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Proxyarr.Tests;

/// <summary>
/// Unit coverage for SABnzbd cross-instance dedup, driving the real proxy (with its SQLite claim
/// store on a temp file) against a WireMock fake SABnzbd. Two dedupe instances (sab1/sab2) plus a
/// dedupe-off instance (plain) share one upstream URL.
/// </summary>
public sealed class SabnzbdDedupeTests : IDisposable
{
    private readonly WireMockServer _upstream = WireMockServer.Start();
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"proxyarr-sabtest-{Guid.NewGuid():N}.db"
    );
    private readonly ProxyAppFactory _factory;
    private readonly HttpClient _client;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SabnzbdDedupeTests()
    {
        _factory = new ProxyAppFactory(
            $"""
            database: {_dbPath}
            clients:
              - name: sab1
                type: sabnzbd
                upstream: {_upstream.Url}
                dedupe:
                  enabled: true
                  category: proxyarr
                  announce_categories: [ann-a, ann-b]
              - name: sab2
                type: sabnzbd
                upstream: {_upstream.Url}
                dedupe:
                  enabled: true
                  category: proxyarr
              - name: plain
                type: sabnzbd
                upstream: {_upstream.Url}
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
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(_dbPath + suffix);
            }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task Duplicate_addfile_reuses_the_existing_job_without_a_second_upstream_add()
    {
        StubAddfile("nzo-1");
        StubQueueSlots("""{"queue":{"slots":[{"nzo_id":"nzo-1","cat":"proxyarr"}]}}""");
        var nzb = Nzb("shared@seg");

        await AddNzb("sab1", nzb, "movies");
        var second = await AddNzb("sab2", nzb, "tv");

        Assert.Single(Requests("addfile")); // only the first instance actually added
        using var json = JsonDocument.Parse(await second.Content.ReadAsStringAsync(Ct));
        Assert.True(json.RootElement.GetProperty("status").GetBoolean());
        Assert.Equal(
            "nzo-1",
            json.RootElement.GetProperty("nzo_ids").EnumerateArray().Single().GetString()
        );
    }

    [Fact]
    public async Task Addfile_translates_the_category_to_the_configured_one()
    {
        StubAddfile("nzo-1");
        await AddNzb("sab1", Nzb("cat@seg"), "movies");

        var forwarded = Requests("addfile").Single().RequestMessage!;
        Assert.Equal("proxyarr", forwarded.Query!["cat"].Single()); // dedupe.category, not "movies"
    }

    [Fact]
    public async Task Queue_listing_rewrites_the_category_for_claimed_slots_only()
    {
        StubAddfile("nzo-1");
        StubQueueSlots("""{"queue":{"slots":[{"nzo_id":"nzo-1","cat":"proxyarr"}]}}""");

        // sab1 grabs it under category "movies"; sab2 never claims it.
        await AddNzb("sab1", Nzb("q@seg"), "movies");

        var forSab1 = await GetJson("sab1", "mode=queue&apikey=k");
        Assert.Equal("movies", SlotCat(forSab1, "queue", "nzo-1"));

        var forSab2 = await GetJson("sab2", "mode=queue&apikey=k");
        Assert.Equal("proxyarr", SlotCat(forSab2, "queue", "nzo-1")); // untouched for the non-owner
    }

    [Fact]
    public async Task History_listing_rewrites_the_category_field()
    {
        StubAddfile("nzo-1");
        StubQueueSlots("""{"queue":{"slots":[{"nzo_id":"nzo-1"}]}}""");
        _upstream
            .Given(Request.Create().WithPath("/api").WithParam("mode", "history").UsingGet())
            .RespondWith(
                Json("""{"history":{"slots":[{"nzo_id":"nzo-1","category":"proxyarr"}]}}""")
            );

        await AddNzb("sab1", Nzb("h@seg"), "movies");

        var history = await GetJson("sab1", "mode=history&apikey=k");
        Assert.Equal("movies", SlotCat(history, "history", "nzo-1"));
    }

    [Fact]
    public async Task Delete_only_forwards_once_the_last_claim_is_gone()
    {
        StubAddfile("nzo-1");
        StubQueueSlots("""{"queue":{"slots":[{"nzo_id":"nzo-1"}]}}""");
        StubDelete();
        var nzb = Nzb("del@seg");

        await AddNzb("sab1", nzb, "movies");
        await AddNzb("sab2", nzb, "tv"); // now two claims on nzo-1

        var first = await GetJson(
            "sab1",
            "mode=queue&name=delete&value=nzo-1&del_files=1&apikey=k"
        );
        Assert.True(first.RootElement.GetProperty("status").GetBoolean());
        Assert.Empty(Requests("queue", name: "delete")); // no upstream delete yet

        await GetJson("sab2", "mode=queue&name=delete&value=nzo-1&del_files=1&apikey=k");
        Assert.Single(Requests("queue", name: "delete")); // last claim → one real delete
    }

    [Fact]
    public async Task Get_config_injects_announced_and_claimed_categories()
    {
        StubAddfile("nzo-1");
        _upstream
            .Given(Request.Create().WithPath("/api").WithParam("mode", "get_config").UsingGet())
            .RespondWith(Json("""{"config":{"categories":[{"name":"existing"}]}}"""));

        await AddNzb("sab1", Nzb("cfg@seg"), "movies"); // claimed category "movies"

        var config = await GetJson("sab1", "mode=get_config&apikey=k");
        var names = config
            .RootElement.GetProperty("config")
            .GetProperty("categories")
            .EnumerateArray()
            .Select(category => category.GetProperty("name").GetString())
            .ToList();

        Assert.Contains("existing", names);
        Assert.Contains("movies", names); // claimed
        Assert.Contains("ann-a", names); // announced
        Assert.Contains("ann-b", names);
    }

    [Fact]
    public async Task Retry_updates_the_tracked_nzo_id()
    {
        StubAddfile("old-nzo");
        StubQueueSlots("""{"queue":{"slots":[{"nzo_id":"new-nzo","cat":"proxyarr"}]}}""");
        _upstream
            .Given(Request.Create().WithPath("/api").WithParam("mode", "retry").UsingGet())
            .RespondWith(Json("""{"status":true,"nzo_ids":["new-nzo"]}"""));

        await AddNzb("sab1", Nzb("retry@seg"), "movies"); // job tracked under old-nzo

        await GetJson("sab1", "mode=retry&value=old-nzo&apikey=k");

        // The job now lives under new-nzo, so the queue listing rewrites that slot's category.
        var queue = await GetJson("sab1", "mode=queue&apikey=k");
        Assert.Equal("movies", SlotCat(queue, "queue", "new-nzo"));
    }

    [Fact]
    public async Task Dedupe_off_instance_forwards_addfile_unchanged()
    {
        StubAddfile("nzo-x");

        await AddNzb("plain", Nzb("plain@seg"), "movies");

        var forwarded = Requests("addfile").Single().RequestMessage!;
        Assert.Equal("movies", forwarded.Query!["cat"].Single()); // category not translated
    }

    // ---- helpers --------------------------------------------------------------------------------

    private async Task<HttpResponseMessage> AddNzb(string prefix, byte[] nzb, string category)
    {
        var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(nzb), "name", "test.nzb" },
        };
        return await _client.PostAsync(
            $"/{prefix}/api?mode=addfile&cat={category}&apikey=k&output=json",
            content,
            Ct
        );
    }

    private async Task<JsonDocument> GetJson(string prefix, string query)
    {
        var body = await _client.GetStringAsync($"/{prefix}/api?{query}&output=json", Ct);
        return JsonDocument.Parse(body);
    }

    private void StubAddfile(string nzoId) =>
        _upstream
            .Given(Request.Create().WithPath("/api").WithParam("mode", "addfile").UsingPost())
            .RespondWith(Json($$"""{"status":true,"nzo_ids":["{{nzoId}}"]}"""));

    private void StubQueueSlots(string body) =>
        _upstream
            .Given(Request.Create().WithPath("/api").WithParam("mode", "queue").UsingGet())
            .AtPriority(10)
            .RespondWith(Json(body));

    private void StubDelete() =>
        _upstream
            .Given(
                Request
                    .Create()
                    .WithPath("/api")
                    .WithParam("mode", "queue")
                    .WithParam("name", "delete")
                    .UsingGet()
            )
            .AtPriority(1)
            .RespondWith(Json("""{"status":true}"""));

    private static IResponseBuilder Json(string body) =>
        Response
            .Create()
            .WithStatusCode(200)
            .WithHeader("Content-Type", "application/json")
            .WithBody(body);

    private static byte[] Nzb(string segmentId) =>
        Encoding.UTF8.GetBytes(
            $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
              <file poster="a@b.c" date="1" subject="s">
                <segments><segment bytes="1" number="1">{segmentId}</segment></segments>
              </file>
            </nzb>
            """
        );

    private static string? SlotCat(JsonDocument doc, string container, string nzoId)
    {
        var slot = doc
            .RootElement.GetProperty(container)
            .GetProperty("slots")
            .EnumerateArray()
            .First(s => s.GetProperty("nzo_id").GetString() == nzoId);
        var field = container == "history" ? "category" : "cat";
        return slot.GetProperty(field).GetString();
    }

    private IReadOnlyList<WireMock.Logging.ILogEntry> Requests(string mode, string? name = null) =>
        _upstream
            .LogEntries.Where(entry =>
            {
                var query = entry.RequestMessage!.Query;
                if (
                    query is null
                    || !query.TryGetValue("mode", out var modes)
                    || !modes.Contains(mode)
                )
                {
                    return false;
                }

                return name is null
                    || (query.TryGetValue("name", out var names) && names.Contains(name));
            })
            .ToList();
}
