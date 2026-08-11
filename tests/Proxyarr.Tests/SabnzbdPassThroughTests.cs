using System.Net;
using System.Text.Json;
using Proxyarr.Tests.Support;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Proxyarr.Tests;

/// <summary>
/// Pass-through behavior for the SABnzbd API surface, using WireMock as a fake SABnzbd instance.
/// SABnzbd is a single /api endpoint dispatched on the 'mode' query parameter.
/// </summary>
public sealed class SabnzbdPassThroughTests : IDisposable
{
    private readonly WireMockServer _upstream;
    private readonly ProxyAppFactory _factory;
    private readonly HttpClient _client;

    public SabnzbdPassThroughTests()
    {
        _upstream = WireMockServer.Start();
        _factory = new ProxyAppFactory(
            $"""
            clients:
              sabnzbd:
                upstreams:
                  - name: main
                    url: {_upstream.Url}
                instances:
                  - name: sab
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

    [Theory]
    [InlineData("version")]
    [InlineData("get_config")]
    [InlineData("fullstatus")]
    [InlineData("queue")]
    [InlineData("history")]
    [InlineData("retry")]
    public async Task Every_allowed_mode_is_forwarded(string mode)
    {
        _upstream
            .Given(Request.Create().WithPath("/api").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"status": true}"""));

        var response = await _client.GetAsync(
            $"/sabnzbd/sab/api?mode={mode}&apikey=secret-key&output=json",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            """{"status": true}""",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );

        var received = Assert.Single(_upstream.LogEntries)!;
        Assert.Equal("/api", received.RequestMessage!.Path);
        Assert.Equal(mode, received.RequestMessage!.Query!["mode"].Single());
        Assert.Equal("secret-key", received.RequestMessage!.Query!["apikey"].Single());
        Assert.Equal("json", received.RequestMessage!.Query!["output"].Single());
    }

    [Fact]
    public async Task Addfile_uploads_are_forwarded_intact()
    {
        _upstream
            .Given(Request.Create().WithPath("/api").UsingPost())
            .RespondWith(
                Response
                    .Create()
                    .WithStatusCode(200)
                    .WithBody("""{"status": true, "nzo_ids": ["SABnzbd_nzo_abc123"]}""")
            );

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("<nzb>fake-nzb-content</nzb>"), "name", "movie.nzb");

        var response = await _client.PostAsync(
            "/sabnzbd/sab/api?mode=addfile&cat=movies&priority=-100&apikey=secret-key&output=json",
            form,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "SABnzbd_nzo_abc123",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );

        var received = Assert.Single(_upstream.LogEntries)!;
        Assert.Equal("addfile", received.RequestMessage!.Query!["mode"].Single());
        Assert.Equal("movies", received.RequestMessage!.Query!["cat"].Single());
        Assert.Equal("-100", received.RequestMessage!.Query!["priority"].Single());
        var body = received.RequestMessage!.Body!;
        Assert.Contains("movie.nzb", body);
        Assert.Contains("<nzb>fake-nzb-content</nzb>", body);
    }

    [Fact]
    public async Task Queue_delete_parameters_pass_through()
    {
        _upstream
            .Given(Request.Create().WithPath("/api").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"status": true}"""));

        await _client.GetAsync(
            "/sabnzbd/sab/api?mode=queue&name=delete&del_files=1&value=SABnzbd_nzo_abc123&apikey=k&output=json",
            TestContext.Current.CancellationToken
        );

        var received = Assert.Single(_upstream.LogEntries)!;
        Assert.Equal("delete", received.RequestMessage!.Query!["name"].Single());
        Assert.Equal("1", received.RequestMessage!.Query!["del_files"].Single());
        Assert.Equal("SABnzbd_nzo_abc123", received.RequestMessage!.Query!["value"].Single());
    }

    [Theory]
    [InlineData("shutdown")] // destructive admin mode
    [InlineData("set_config")] // config mutation
    [InlineData("restart")]
    [InlineData("addurl")] // real mode, but not used by Radarr
    public async Task Modes_outside_the_allow_list_are_rejected_without_forwarding(string mode)
    {
        var response = await _client.GetAsync(
            $"/sabnzbd/sab/api?mode={mode}&apikey=secret-key",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_upstream.LogEntries);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);
        Assert.False(json.RootElement.GetProperty("status").GetBoolean());
        Assert.Contains(mode, json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Missing_mode_is_rejected_without_forwarding()
    {
        var response = await _client.GetAsync(
            "/sabnzbd/sab/api?apikey=secret-key",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_upstream.LogEntries);
    }

    [Fact]
    public async Task Paths_other_than_api_are_not_forwarded()
    {
        var response = await _client.GetAsync(
            "/sabnzbd/sab/sabnzbd/config/general/",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_upstream.LogEntries);
    }
}
