using System.Net;
using System.Text;
using System.Text.Json;
using Proxyarr.IntegrationTests.Support;

namespace Proxyarr.IntegrationTests;

/// <summary>
/// Exercises SABnzbd cross-instance dedup against a real SABnzbd 5.0.4 container. Two dedupe
/// instances (sab1/sab2, category <c>proxyarr-it</c>) share the container; each test uses a unique
/// NZB so its content key and nzo_id never collide with another test's.
/// </summary>
public sealed class SabnzbdDedupeIntegrationTests
    : IClassFixture<SabnzbdContainerFixture>,
        IDisposable
{
    private const string ApiKey = SabnzbdContainerFixture.ApiKey;

    private readonly string _upstreamUrl;
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"proxyarr-sabit-{Guid.NewGuid():N}.db"
    );
    private readonly ProxyAppFactory? _factory;
    private readonly HttpClient? _client;
    private readonly HttpClient _raw = new();

    public SabnzbdDedupeIntegrationTests(SabnzbdContainerFixture sabnzbd)
    {
        Assert.SkipWhen(sabnzbd.SkipReason is not null, sabnzbd.SkipReason ?? "");
        _upstreamUrl = sabnzbd.UpstreamUrl;

        _factory = new ProxyAppFactory(
            $"""
            database: {_dbPath}
            clients:
              - name: sab1
                type: sabnzbd
                upstream: {_upstreamUrl}
                dedupe:
                  enabled: true
                  category: proxyarr-it
                  group: main
              - name: sab2
                type: sabnzbd
                upstream: {_upstreamUrl}
                dedupe:
                  enabled: true
                  category: proxyarr-it
                  group: main
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
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
    public async Task Same_nzb_from_two_instances_is_added_once_and_shared()
    {
        var ct = TestContext.Current.CancellationToken;
        var nzb = Nzb($"dup-{Guid.NewGuid():N}@example.invalid");

        var nzo1 = await AddNzb("sab1", nzb, "movies", ct);
        var nzo2 = await AddNzb("sab2", nzb, "tv", ct);

        Assert.Equal(nzo1, nzo2); // second grab reused the first job

        // Exactly one job upstream, seen by both.
        Assert.True(await RawJobExistsAsync(nzo1, ct), "the shared job should exist upstream");

        // Each instance sees its own category in the queue/history listing.
        Assert.Equal("movies", await ProxyCategoryAsync("sab1", nzo1, ct));
        Assert.Equal("tv", await ProxyCategoryAsync("sab2", nzo1, ct));
    }

    [Fact]
    public async Task Delete_keeps_the_job_until_the_last_claim_is_removed()
    {
        var ct = TestContext.Current.CancellationToken;
        var nzb = Nzb($"del-{Guid.NewGuid():N}@example.invalid");

        var nzo = await AddNzb("sab1", nzb, "movies", ct);
        await AddNzb("sab2", nzb, "tv", ct);
        Assert.True(await RawJobExistsAsync(nzo, ct));

        // First instance leaves: the job survives upstream.
        await DeleteAsync("sab1", nzo, ct);
        Assert.True(
            await RawJobExistsAsync(nzo, ct),
            "job should survive while sab2 still claims it"
        );

        // Last instance leaves: the job is really removed.
        await DeleteAsync("sab2", nzo, ct);
        await WaitForAbsentAsync(nzo, ct);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private async Task<string> AddNzb(
        string prefix,
        byte[] nzb,
        string category,
        CancellationToken ct
    )
    {
        var content = new ByteArrayContent(nzb);
        content.Headers.ContentType = new("application/x-nzb");
        using var form = new MultipartFormDataContent { { content, "name", "test.nzb" } };

        // priority=-2 (paused) keeps the job parked: the container has no Usenet servers.
        var response = await Client.PostAsync(
            $"/{prefix}/api?mode=addfile&cat={category}&priority=-2&apikey={ApiKey}&output=json",
            form,
            ct
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.True(json.RootElement.GetProperty("status").GetBoolean());
        return json.RootElement.GetProperty("nzo_ids").EnumerateArray().Single().GetString()!;
    }

    private async Task DeleteAsync(string prefix, string nzoId, CancellationToken ct)
    {
        // The job lands in queue or (if SAB fails it immediately) history; try both.
        var mode = await RawJobLocationAsync(nzoId, ct) ?? "queue";
        var response = await Client.GetStringAsync(
            $"/{prefix}/api?mode={mode}&name=delete&value={nzoId}&del_files=1&apikey={ApiKey}&output=json",
            ct
        );
        using var json = JsonDocument.Parse(response);
        Assert.True(json.RootElement.GetProperty("status").GetBoolean());
    }

    private async Task<string?> ProxyCategoryAsync(
        string prefix,
        string nzoId,
        CancellationToken ct
    )
    {
        foreach (
            var (mode, container, field) in new[]
            {
                ("queue", "queue", "cat"),
                ("history", "history", "category"),
            }
        )
        {
            var json = await Client.GetStringAsync(
                $"/{prefix}/api?mode={mode}&apikey={ApiKey}&output=json",
                ct
            );
            using var doc = JsonDocument.Parse(json);
            var slot = doc
                .RootElement.GetProperty(container)
                .GetProperty("slots")
                .EnumerateArray()
                .FirstOrDefault(s => s.GetProperty("nzo_id").GetString() == nzoId);
            if (slot.ValueKind == JsonValueKind.Object)
            {
                return slot.GetProperty(field).GetString();
            }
        }

        return null;
    }

    private async Task<bool> RawJobExistsAsync(string nzoId, CancellationToken ct) =>
        await RawJobLocationAsync(nzoId, ct) is not null;

    private async Task<string?> RawJobLocationAsync(string nzoId, CancellationToken ct)
    {
        foreach (var (mode, container) in new[] { ("queue", "queue"), ("history", "history") })
        {
            var json = await _raw.GetStringAsync(
                $"{_upstreamUrl}/api?mode={mode}&start=0&limit=100&apikey={ApiKey}&output=json",
                ct
            );
            using var doc = JsonDocument.Parse(json);
            var present = doc
                .RootElement.GetProperty(container)
                .GetProperty("slots")
                .EnumerateArray()
                .Any(slot => slot.GetProperty("nzo_id").GetString() == nzoId);
            if (present)
            {
                return mode;
            }
        }

        return null;
    }

    private async Task WaitForAbsentAsync(string nzoId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (!await RawJobExistsAsync(nzoId, ct))
            {
                return;
            }

            await Task.Delay(300, ct);
        }

        Assert.Fail($"Job {nzoId} is still present after deletion.");
    }

    private static byte[] Nzb(string segmentId) =>
        Encoding.UTF8.GetBytes(
            $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
              <file poster="test@example.invalid" date="1234567890" subject="&quot;proxyarr.bin&quot; yEnc (1/1)">
                <groups><group>alt.binaries.test</group></groups>
                <segments><segment bytes="1024" number="1">{segmentId}</segment></segments>
              </file>
            </nzb>
            """
        );
}
