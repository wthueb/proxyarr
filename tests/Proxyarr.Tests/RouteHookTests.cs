using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Proxyarr.Clients;
using Proxyarr.Tests.Support;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Proxyarr.Tests;

/// <summary>
/// Exercises the optional <see cref="ProxyRoute"/> hooks (OnRequest, TransformRequest,
/// TransformResponse, Handle) through a stub adapter registered on top of the real ones.
/// </summary>
public sealed class RouteHookTests : IDisposable
{
    private readonly WireMockServer _upstream = WireMockServer.Start();
    private readonly CapturingLoggerProvider _logs = new();
    private ProxyAppFactory? _factory;
    private HttpClient? _client;

    private HttpClient CreateClient(params ProxyRoute[] routes)
    {
        _factory = new ProxyAppFactory(
            $"""
            clients:
              - name: stub
                type: stub
                upstream: {_upstream.Url}
            """,
            _logs,
            services => services.AddSingleton<IDownloadClientAdapter>(new StubAdapter(routes))
        );
        _client = _factory.CreateClient();
        return _client;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
        _upstream.Stop();
        _upstream.Dispose();
    }

    [Fact]
    public async Task OnRequest_reads_the_multipart_body_and_the_upstream_still_receives_it()
    {
        _upstream
            .Given(Request.Create().WithPath("/echo").UsingPost())
            .RespondWith(Response.Create().WithBody("ok"));
        byte[]? seenByHook = null;
        var client = CreateClient(
            new ProxyRoute(
                "/echo",
                ["POST"],
                OnRequest: async (context, _) =>
                {
                    using var buffer = new MemoryStream();
                    await context.Request.Body.CopyToAsync(buffer);
                    seenByHook = buffer.ToArray();
                    return null;
                }
            )
        );

        // Mimics qBittorrent's torrents/add: a binary file part plus form fields.
        var torrentBytes = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(torrentBytes), "torrents", "test.torrent");
        content.Add(new StringContent("movies"), "category");
        var sentBytes = await content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        var response = await client.PostAsync(
            "/stub/echo",
            content,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(sentBytes, seenByHook);
        var received = Assert.Single(_upstream.LogEntries)!;
        Assert.Equal(sentBytes, received.RequestMessage!.BodyAsBytes);
    }

    [Fact]
    public async Task OnRequest_can_short_circuit_without_contacting_the_upstream()
    {
        var client = CreateClient(
            new ProxyRoute(
                "/blocked",
                ["GET"],
                OnRequest: (_, _) =>
                    ValueTask.FromResult<IResult?>(
                        Results.Json(
                            new { blocked = true },
                            statusCode: StatusCodes.Status403Forbidden
                        )
                    )
            )
        );

        var response = await client.GetAsync(
            "/stub/blocked",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_upstream.LogEntries);
        Assert.Contains(
            _logs.Events,
            logEvent =>
                logEvent.Level == LogLevel.Information
                && logEvent.Message.StartsWith("Handled")
                && logEvent.Message.Contains("short-circuited by the OnRequest hook")
        );
    }

    [Fact]
    public async Task TransformRequest_can_rewrite_what_goes_upstream()
    {
        _upstream
            .Given(Request.Create().WithPath("/info").UsingGet())
            .RespondWith(Response.Create().WithBody("ok"));
        var client = CreateClient(
            new ProxyRoute(
                "/info",
                ["GET"],
                TransformRequest: (_, _, proxyRequest) =>
                {
                    proxyRequest.Headers.Add("X-Proxyarr", "injected");
                    return ValueTask.CompletedTask;
                }
            )
        );

        await client.GetAsync("/stub/info", TestContext.Current.CancellationToken);

        var received = Assert.Single(_upstream.LogEntries)!;
        Assert.Equal("injected", received.RequestMessage!.Headers!["X-Proxyarr"].Single());
    }

    [Fact]
    public async Task TransformResponse_can_rewrite_the_upstream_body()
    {
        _upstream
            .Given(Request.Create().WithPath("/version").UsingGet())
            .RespondWith(Response.Create().WithBody("v1.0-upstream"));
        var client = CreateClient(
            new ProxyRoute(
                "/version",
                ["GET"],
                TransformResponse: async (context, _, proxyResponse) =>
                {
                    if (proxyResponse is null)
                    {
                        return true;
                    }

                    var body = await proxyResponse.Content.ReadAsStringAsync();
                    var rewritten = Encoding.UTF8.GetBytes(body.Replace("upstream", "proxyarr"));
                    context.Response.ContentLength = rewritten.Length;
                    await context.Response.Body.WriteAsync(rewritten);
                    return false;
                }
            )
        );

        var response = await client.GetAsync(
            "/stub/version",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            "v1.0-proxyarr",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        Assert.Equal("v1.0-proxyarr".Length, response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task Handle_answers_locally_and_logs_an_outcome()
    {
        var client = CreateClient(
            new ProxyRoute(
                "/local/status",
                ["GET"],
                Handle: (_, instance) =>
                    Task.FromResult(Results.Json(new { instanceName = instance.Name, ok = true }))
            )
        );

        var body = await client.GetStringAsync(
            "/stub/local/status",
            TestContext.Current.CancellationToken
        );

        Assert.Contains("\"instanceName\":\"stub\"", body);
        Assert.Contains("\"ok\":true", body);
        Assert.Empty(_upstream.LogEntries);
        var outcome = Assert.Single(
            _logs.Events,
            logEvent =>
                logEvent.Category == "Proxyarr.Requests" && logEvent.Message.StartsWith("Handled")
        );
        Assert.Equal("stub", outcome.Fields["Instance"]);
        Assert.Equal(200, outcome.Fields["StatusCode"]);
    }

    private sealed class StubAdapter(IReadOnlyList<ProxyRoute> routes) : IDownloadClientAdapter
    {
        public string Type => "stub";

        public IReadOnlyList<ProxyRoute> GetRoutes(
            Proxyarr.Configuration.ClientInstanceConfig instance
        ) => routes;
    }
}
