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
        using var factory = new ProxyAppFactory("clients: []");
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
        using var factory = new ProxyAppFactory("clients: []");
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            "/qbit/api/v2/app/version",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
              - name: qbit-movies
                type: qbittorrent
                upstream: {upstream1.Url}
              - name: qbit-4k
                type: qbittorrent
                upstream: {upstream2.Url}
            """
        );
        using var client = factory.CreateClient();

        Assert.Equal(
            "v5.2.3-instance-one",
            await client.GetStringAsync(
                "/qbit-movies/api/v2/app/version",
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal(
            "v5.2.3-instance-two",
            await client.GetStringAsync(
                "/qbit-4k/api/v2/app/version",
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
              - name: sab
                type: sabnzbd
                upstream: {upstream.Url}/sabnzbd
            """
        );
        using var client = factory.CreateClient();

        var body = await client.GetStringAsync(
            "/sab/api?mode=version",
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
              - name: qbit
                type: qbittorrent
                upstream: http://127.0.0.1:9
            """
        );
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            "/qbit/api/v2/app/version",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public void Unknown_client_types_fail_at_startup()
    {
        using var factory = new ProxyAppFactory(
            """
            clients:
              - name: mystery
                type: rtorrent
                upstream: http://localhost:8080
            """
        );

        var ex = Assert.Throws<ConfigurationException>(() => factory.CreateClient());

        Assert.Contains("rtorrent", ex.Message);
        Assert.Contains("qbittorrent", ex.Message);
        Assert.Contains("sabnzbd", ex.Message);
    }
}
