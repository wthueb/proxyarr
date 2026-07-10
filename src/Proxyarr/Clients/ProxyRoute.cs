using Proxyarr.Configuration;

namespace Proxyarr.Clients;

/// <summary>
/// One upstream endpoint exposed through the proxy. Anything not declared as a route returns 404,
/// so each adapter is an explicit allow-list of what the proxy will forward.
///
/// Hooks run in lifecycle order: <see cref="Validate"/> first, then <see cref="Handle"/> (which
/// owns the whole request when set — nothing else runs), then <see cref="OnRequest"/>, and finally
/// the forward itself with <see cref="TransformRequest"/> applied to the outgoing request and
/// <see cref="TransformResponse"/> to the upstream's response.
/// </summary>
/// <param name="Pattern">
/// Path relative to both the instance's route prefix and the upstream base URL,
/// e.g. <c>/api/v2/torrents/info</c>.
/// </param>
/// <param name="Methods">Accepted HTTP methods; other methods on the same path return 405.</param>
/// <param name="Validate">
/// Optional guard evaluated before forwarding. Returning a non-null <see cref="IResult"/>
/// short-circuits the request with that response instead of contacting the upstream.
/// </param>
/// <param name="OnRequest">
/// Optional async hook that runs after <paramref name="Validate"/> and before forwarding. The
/// request body is buffered and rewound afterwards, so the hook may read it freely. Returning a
/// non-null <see cref="IResult"/> short-circuits the request instead of contacting the upstream.
/// </param>
/// <param name="TransformRequest">
/// Optional mutation of the outgoing upstream request, invoked after the prefix-strip/URI rewrite
/// so it sees the final destination. May change headers, the URI, or replace
/// <see cref="HttpRequestMessage.Content"/>.
/// </param>
/// <param name="TransformResponse">
/// Optional mutation of the upstream's response, invoked after the status code and headers have
/// been copied to the client response (the <see cref="HttpResponseMessage"/> is null when the
/// upstream could not be reached). Return true to let the upstream body be copied as usual, or
/// write the body yourself and return false — in that case the hook must also fix
/// <c>Content-Length</c> when it changes the payload.
/// </param>
/// <param name="Handle">
/// Optional fully local endpoint: produce the response yourself; the request is never forwarded
/// and no other hook runs. Use the instance's upstream URL plus an injected
/// <see cref="HttpClient"/> to make your own upstream calls if needed.
/// </param>
public sealed record ProxyRoute(
    string Pattern,
    IReadOnlyList<string> Methods,
    Func<HttpRequest, IResult?>? Validate = null,
    Func<HttpContext, ClientInstanceConfig, ValueTask<IResult?>>? OnRequest = null,
    Func<HttpContext, HttpRequestMessage, ValueTask>? TransformRequest = null,
    Func<HttpContext, HttpResponseMessage?, ValueTask<bool>>? TransformResponse = null,
    Func<HttpContext, ClientInstanceConfig, Task<IResult>>? Handle = null
)
{
    public static ProxyRoute Get(string pattern) => new(pattern, ["GET"]);

    public static ProxyRoute Post(string pattern) => new(pattern, ["POST"]);
}
