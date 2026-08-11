using Yarp.ReverseProxy.Forwarder;

namespace Proxyarr.Forwarding;

/// <summary>
/// Rewrites <c>/{type}/{instance-name}/some/path</c> to <c>{upstream}/some/path</c> while keeping the
/// query string and (via the base transformer) all headers and the body intact. The Host header is
/// set to the upstream's authority, which keeps qBittorrent's host header validation happy.
/// Optional per-route transforms run on top: <paramref name="transformRequest"/> after the URI
/// rewrite, <paramref name="transformResponse"/> after status and headers are copied (returning
/// false suppresses the default upstream body copy).
/// </summary>
public sealed class PrefixStripTransformer(
    PathString prefix,
    Func<HttpContext, HttpRequestMessage, ValueTask>? transformRequest = null,
    Func<HttpContext, HttpResponseMessage?, ValueTask<bool>>? transformResponse = null
) : HttpTransformer
{
    public override async ValueTask TransformRequestAsync(
        HttpContext httpContext,
        HttpRequestMessage proxyRequest,
        string destinationPrefix,
        CancellationToken cancellationToken
    )
    {
        await base.TransformRequestAsync(
            httpContext,
            proxyRequest,
            destinationPrefix,
            cancellationToken
        );

        httpContext.Request.Path.StartsWithSegments(prefix, out var upstreamPath);
        proxyRequest.RequestUri = RequestUtilities.MakeDestinationAddress(
            destinationPrefix,
            upstreamPath,
            httpContext.Request.QueryString
        );

        if (transformRequest is not null)
        {
            await transformRequest(httpContext, proxyRequest);
        }
    }

    public override async ValueTask<bool> TransformResponseAsync(
        HttpContext httpContext,
        HttpResponseMessage? proxyResponse,
        CancellationToken cancellationToken
    )
    {
        var copyBody = await base.TransformResponseAsync(
            httpContext,
            proxyResponse,
            cancellationToken
        );

        if (transformResponse is not null)
        {
            copyBody &= await transformResponse(httpContext, proxyResponse);
        }

        return copyBody;
    }
}
