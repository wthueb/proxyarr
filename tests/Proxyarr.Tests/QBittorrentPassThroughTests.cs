using System.Net;
using System.Net.Http.Headers;
using Proxyarr.Clients.QBittorrent;
using Proxyarr.Tests.Support;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Proxyarr.Tests;

/// <summary>
/// Pass-through behavior for the qBittorrent Web API v2 surface, using WireMock as a fake
/// qBittorrent instance.
/// </summary>
public sealed class QBittorrentPassThroughTests : IDisposable
{
    private readonly WireMockServer _upstream;
    private readonly ProxyAppFactory _factory;
    private readonly HttpClient _client;

    public QBittorrentPassThroughTests()
    {
        _upstream = WireMockServer.Start();
        _factory = new ProxyAppFactory(
            $"""
            clients:
              qbittorrent:
                upstreams:
                  - name: main
                    url: {_upstream.Url}
                instances:
                  - name: qbit
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

    public static TheoryData<string, string> AllDeclaredEndpoints()
    {
        var data = new TheoryData<string, string>();
        foreach (var route in QBittorrentAdapter.PassThroughRoutes)
        {
            foreach (var method in route.Methods)
            {
                data.Add(method, route.Pattern);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllDeclaredEndpoints))]
    public async Task Every_declared_endpoint_is_forwarded(string method, string path)
    {
        _upstream
            .Given(Request.Create().WithPath(path).UsingMethod(method))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("upstream-response"));

        using var request = new HttpRequestMessage(
            HttpMethod.Parse(method),
            $"/qbittorrent/qbit{path}"
        );
        if (method == "POST")
        {
            request.Content = new FormUrlEncodedContent([]);
        }

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "upstream-response",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );

        var received = Assert.Single(_upstream.LogEntries)!;
        Assert.Equal(path, received.RequestMessage!.Path);
    }

    [Fact]
    public async Task Login_forwards_credentials_and_returns_the_session_cookie()
    {
        _upstream
            .Given(Request.Create().WithPath("/api/v2/auth/login").UsingPost())
            .RespondWith(
                Response
                    .Create()
                    .WithStatusCode(200)
                    .WithHeader("Set-Cookie", "SID=abc123sessionid; HttpOnly; path=/")
                    .WithBody("Ok.")
            );

        var response = await _client.PostAsync(
            "/qbittorrent/qbit/api/v2/auth/login",
            new FormUrlEncodedContent(
                new Dictionary<string, string> { ["username"] = "admin", ["password"] = "secret" }
            ),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "Ok.",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("SID=abc123sessionid", setCookie);

        var received = Assert.Single(_upstream.LogEntries)!;
        Assert.Equal("username=admin&password=secret", received.RequestMessage!.Body);
    }

    [Fact]
    public async Task Session_cookies_are_forwarded_to_the_upstream()
    {
        _upstream
            .Given(Request.Create().WithPath("/api/v2/torrents/info").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("[]"));

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/qbittorrent/qbit/api/v2/torrents/info"
        );
        request.Headers.Add("Cookie", "SID=abc123sessionid");

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var received = Assert.Single(_upstream.LogEntries)!;
        Assert.Equal("SID=abc123sessionid", received.RequestMessage!.Headers!["Cookie"].Single());
    }

    [Fact]
    public async Task Query_strings_are_preserved()
    {
        _upstream
            .Given(Request.Create().WithPath("/api/v2/torrents/info").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("[]"));

        await _client.GetAsync(
            "/qbittorrent/qbit/api/v2/torrents/info?category=radarr&hashes=abcd1234",
            TestContext.Current.CancellationToken
        );

        var received = Assert.Single(_upstream.LogEntries)!;
        Assert.Equal("radarr", received.RequestMessage!.Query!["category"].Single());
        Assert.Equal("abcd1234", received.RequestMessage!.Query!["hashes"].Single());
    }

    [Fact]
    public async Task Torrent_file_uploads_are_forwarded_intact()
    {
        _upstream
            .Given(Request.Create().WithPath("/api/v2/torrents/add").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("Ok."));

        var torrentBytes = "d8:announce9:fake-data4:infod4:name5:movieee"u8.ToArray();
        var torrentContent = new ByteArrayContent(torrentBytes);
        torrentContent.Headers.ContentType = new MediaTypeHeaderValue("application/x-bittorrent");

        using var form = new MultipartFormDataContent();
        form.Add(torrentContent, "torrents", "movie.torrent");
        form.Add(new StringContent("radarr"), "category");
        form.Add(new StringContent("true"), "stopped");

        var response = await _client.PostAsync(
            "/qbittorrent/qbit/api/v2/torrents/add",
            form,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var received = Assert.Single(_upstream.LogEntries)!;
        Assert.StartsWith(
            "multipart/form-data",
            received.RequestMessage!.Headers!["Content-Type"].Single()
        );
        var body = received.RequestMessage!.Body!;
        Assert.Contains("movie.torrent", body);
        Assert.Contains("d8:announce9:fake-data", body);
        Assert.Contains("radarr", body);
    }

    [Fact]
    public async Task Upstream_errors_pass_through_unchanged()
    {
        _upstream
            .Given(Request.Create().WithPath("/api/v2/torrents/info").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(403).WithBody("Forbidden"));

        var response = await _client.GetAsync(
            "/qbittorrent/qbit/api/v2/torrents/info",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            "Forbidden",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
    }

    [Theory]
    [InlineData("POST", "/api/v2/torrents/pause")] // pre-2.11 name, removed in qBittorrent 5.x
    [InlineData("POST", "/api/v2/torrents/resume")]
    [InlineData("GET", "/api/v2/sync/maindata")] // real endpoint, but not used by Radarr
    [InlineData("POST", "/api/v2/app/setPreferences")] // mutating admin endpoint, never proxied
    [InlineData("GET", "/query/torrents")] // API v1, unsupported
    public async Task Endpoints_outside_the_allow_list_are_not_forwarded(string method, string path)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Parse(method),
            $"/qbittorrent/qbit{path}"
        );
        if (method == "POST")
        {
            request.Content = new FormUrlEncodedContent([]);
        }

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_upstream.LogEntries);
    }

    [Fact]
    public async Task Wrong_methods_are_rejected_without_forwarding()
    {
        var response = await _client.GetAsync(
            "/qbittorrent/qbit/api/v2/torrents/delete",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Empty(_upstream.LogEntries);
    }
}
