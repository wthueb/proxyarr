using System.Diagnostics;
using System.Net;
using Proxyarr.Configuration;
using Proxyarr.Logging;
using Yarp.ReverseProxy.Forwarder;

namespace Proxyarr.Forwarding;

/// <summary>
/// Streams requests to an upstream download client using YARP's low-level forwarder, which handles
/// hop-by-hop headers, bidirectional streaming, and error mapping (unreachable upstreams become
/// 502 responses). Every proxied request is logged with its outcome and duration; query strings
/// are redacted before logging.
/// </summary>
public sealed class UpstreamForwarder(IHttpForwarder forwarder, ILogger<UpstreamForwarder> logger)
    : IDisposable
{
    /// <summary>
    /// Set on <see cref="HttpContext.Items"/> once a request has been handed to the upstream, so
    /// later pipeline stages can tell an upstream 404 from "no such proxy route".
    /// </summary>
    public const string ForwardedItemKey = "proxyarr.forwarded";

    private static readonly ForwarderRequestConfig RequestConfig = new()
    {
        ActivityTimeout = TimeSpan.FromMinutes(2),
    };

    private readonly HttpMessageInvoker _httpClient = new(
        new SocketsHttpHandler
        {
            // Pure pass-through: no proxying of the outbound call, no cookie jar (session cookies
            // such as qBittorrent's SID must flow between Radarr and the upstream untouched), no
            // transparent decompression, and no redirect following.
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            ActivityHeadersPropagator = null,
        }
    );

    public async Task ForwardAsync(
        HttpContext context,
        ClientInstanceConfig instance,
        HttpTransformer transformer
    )
    {
        context.Items[ForwardedItemKey] = true;

        logger.LogDebug("Forwarding request", ("Upstream", instance.Upstream));

        var startTimestamp = Stopwatch.GetTimestamp();
        var error = await forwarder.SendAsync(
            context,
            instance.Upstream,
            _httpClient,
            RequestConfig,
            transformer
        );
        var elapsedMs = Math.Round(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds, 1);

        if (error == ForwarderError.None)
        {
            logger.LogInformation(
                "Request proxied",
                ("StatusCode", context.Response.StatusCode),
                ("ElapsedMs", elapsedMs)
            );
        }
        else
        {
            var exception = context.Features.Get<IForwarderErrorFeature>()?.Exception;
            logger.LogError(
                exception,
                "Request proxy failed",
                ("Upstream", instance.Upstream),
                ("ElapsedMs", elapsedMs),
                ("Error", error)
            );
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
