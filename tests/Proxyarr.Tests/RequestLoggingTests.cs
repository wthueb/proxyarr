using Microsoft.Extensions.Logging;
using Proxyarr.Tests.Support;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Proxyarr.Tests;

/// <summary>End-to-end checks that proxied traffic produces useful, safe structured logs.</summary>
public sealed class RequestLoggingTests : IDisposable
{
    private readonly WireMockServer _upstream;
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ProxyAppFactory _factory;
    private readonly HttpClient _client;

    public RequestLoggingTests()
    {
        _upstream = WireMockServer.Start();
        _upstream
            .Given(Request.Create().WithPath("/api/v2/app/version").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("v5.2.3"));
        _upstream
            .Given(Request.Create().WithPath("/api").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"status": true}"""));

        _factory = new ProxyAppFactory(
            $"""
            logging:
              level: debug
            clients:
              - name: qbit
                type: qbittorrent
                upstream: {_upstream.Url}
              - name: sab
                type: sabnzbd
                upstream: {_upstream.Url}
            """,
            _logs
        );
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _upstream.Stop();
        _upstream.Dispose();
    }

    [Fact]
    public async Task Proxied_requests_emit_structured_completion_logs()
    {
        await _client.GetAsync("/qbit/api/v2/app/version", TestContext.Current.CancellationToken);

        var completion = Assert.Single(
            _logs.Events,
            logEvent =>
                logEvent.Level == LogLevel.Information && logEvent.Message.StartsWith("Proxied")
        );
        Assert.Equal("Proxyarr.Forwarding.UpstreamForwarder", completion.Category);
        Assert.Equal("qbit", completion.Fields["Instance"]);
        Assert.Equal("GET", completion.Fields["Method"]);
        Assert.Equal("/qbit/api/v2/app/version", completion.Fields["Path"]);
        Assert.Equal(200, completion.Fields["StatusCode"]);
        Assert.True(completion.Fields.ContainsKey("ElapsedMs"));
    }

    [Fact]
    public async Task Sensitive_query_values_never_reach_the_logs()
    {
        await _client.GetAsync(
            "/sab/api?mode=version&apikey=supersecretvalue",
            TestContext.Current.CancellationToken
        );

        Assert.DoesNotContain(
            _logs.Events,
            logEvent =>
                logEvent.Message.Contains("supersecretvalue")
                || logEvent.Fields.Values.Any(value =>
                    value?.ToString()?.Contains("supersecretvalue") == true
                )
        );
        Assert.Contains(
            _logs.Events,
            logEvent =>
                logEvent.Fields.TryGetValue("Query", out var query)
                && query?.ToString() == "?mode=version&apikey=REDACTED"
        );
    }

    [Fact]
    public async Task Unmatched_endpoints_log_a_warning_naming_the_request()
    {
        await _client.GetAsync("/qbit/api/v2/sync/maindata", TestContext.Current.CancellationToken);

        var warning = Assert.Single(
            _logs.Events,
            logEvent =>
                logEvent.Level == LogLevel.Warning
                && logEvent.Message.StartsWith("No proxied endpoint matches")
        );
        Assert.Equal("Proxyarr.Requests", warning.Category);
        Assert.Equal("/qbit/api/v2/sync/maindata", warning.Fields["Path"]);
    }

    [Fact]
    public async Task Guard_rejections_log_a_warning()
    {
        await _client.GetAsync(
            "/sab/api?mode=shutdown&apikey=secret",
            TestContext.Current.CancellationToken
        );

        var warning = Assert.Single(
            _logs.Events,
            logEvent =>
                logEvent.Level == LogLevel.Warning && logEvent.Message.StartsWith("Rejected")
        );
        Assert.Equal("sab", warning.Fields["Instance"]);
    }
}
