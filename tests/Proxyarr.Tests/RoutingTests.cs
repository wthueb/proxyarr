using System.Net;
using Proxyarr.Configuration;
using Proxyarr.Tests.Support;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Proxyarr.Tests;

public class RoutingTests
{
    [Fact]
    public async Task Health_endpoint_responds_without_any_clients_configured()
    {
        using var factory = new ProxyAppFactory("clients: {}");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "OK",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task Unknown_instance_prefixes_return_404()
    {
        using var factory = new ProxyAppFactory("clients: {}");
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            "/qbittorrent/qbit/api/v2/app/version",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_same_name_routes_independently_under_each_client_type()
    {
        using var upstream = WireMockServer.Start();
        upstream
            .Given(Request.Create().WithPath("/api/v2/app/version").UsingGet())
            .RespondWith(Response.Create().WithBody("qBittorrent"));
        upstream
            .Given(Request.Create().WithPath("/api").WithParam("mode", "version").UsingGet())
            .RespondWith(Response.Create().WithBody("SABnzbd"));

        using var factory = new ProxyAppFactory(
            $"""
            clients:
              qbittorrent:
                upstreams:
                  - name: main
                    url: {upstream.Url}
                instances:
                  - name: radarr
                    upstream: main
              sabnzbd:
                upstreams:
                  - name: main
                    url: {upstream.Url}
                instances:
                  - name: radarr
                    upstream: main
            """
        );
        using var client = factory.CreateClient();

        Assert.Equal(
            "qBittorrent",
            await client.GetStringAsync(
                "/qbittorrent/radarr/api/v2/app/version",
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal(
            "SABnzbd",
            await client.GetStringAsync(
                "/sabnzbd/radarr/api?mode=version",
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public async Task Multiple_instances_of_the_same_type_route_to_their_own_upstreams()
    {
        using var upstream1 = WireMockServer.Start();
        using var upstream2 = WireMockServer.Start();
        upstream1
            .Given(Request.Create().WithPath("/api/v2/app/version").UsingGet())
            .RespondWith(Response.Create().WithBody("v5.2.3-instance-one"));
        upstream2
            .Given(Request.Create().WithPath("/api/v2/app/version").UsingGet())
            .RespondWith(Response.Create().WithBody("v5.2.3-instance-two"));

        using var factory = new ProxyAppFactory(
            $"""
            clients:
              qbittorrent:
                upstreams:
                  - name: movies
                    url: {upstream1.Url}
                  - name: four-k
                    url: {upstream2.Url}
                instances:
                  - name: qbit-movies
                    upstream: movies
                  - name: qbit-4k
                    upstream: four-k
            """
        );
        using var client = factory.CreateClient();

        Assert.Equal(
            "v5.2.3-instance-one",
            await client.GetStringAsync(
                "/qbittorrent/qbit-movies/api/v2/app/version",
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal(
            "v5.2.3-instance-two",
            await client.GetStringAsync(
                "/qbittorrent/qbit-4k/api/v2/app/version",
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public async Task Upstreams_with_a_base_path_are_supported()
    {
        using var upstream = WireMockServer.Start();
        upstream
            .Given(Request.Create().WithPath("/sabnzbd/api").UsingGet())
            .RespondWith(Response.Create().WithBody("""{"version": "5.0.4"}"""));

        using var factory = new ProxyAppFactory(
            $"""
            clients:
              sabnzbd:
                upstreams:
                  - name: main
                    url: {upstream.Url}/sabnzbd
                instances:
                  - name: sab
                    upstream: main
            """
        );
        using var client = factory.CreateClient();

        var body = await client.GetStringAsync(
            "/sabnzbd/sab/api?mode=version",
            TestContext.Current.CancellationToken
        );

        Assert.Equal("""{"version": "5.0.4"}""", body);
        var received = Assert.Single(upstream.LogEntries)!;
        Assert.Equal("/sabnzbd/api", received.RequestMessage!.Path);
        Assert.Equal("version", received.RequestMessage!.Query!["mode"].Single());
    }

    [Fact]
    public async Task Unreachable_upstreams_return_502()
    {
        // Port 9 (discard) is reserved and nothing listens on it locally.
        using var factory = new ProxyAppFactory(
            """
            clients:
              qbittorrent:
                upstreams:
                  - name: main
                    url: http://127.0.0.1:9
                instances:
                  - name: qbit
                    upstream: main
            """
        );
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            "/qbittorrent/qbit/api/v2/app/version",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }
}
