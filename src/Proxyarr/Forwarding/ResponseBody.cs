using System.Text;

namespace Proxyarr.Forwarding;

/// <summary>
/// Helper for a <see cref="ProxyRoute.TransformResponse"/> hook that wants to replace the upstream
/// body wholesale: it fixes <c>Content-Length</c>, optionally overrides the content type, writes the
/// new payload, and returns <c>false</c> so the default upstream body copy is suppressed.
/// </summary>
public static class ResponseBody
{
    public static async ValueTask<bool> ReplaceAsync(
        HttpContext context,
        ReadOnlyMemory<byte> payload,
        string? contentType = null
    )
    {
        context.Response.ContentLength = payload.Length;
        if (contentType is not null)
        {
            context.Response.ContentType = contentType;
        }

        await context.Response.Body.WriteAsync(payload);
        return false;
    }

    public static ValueTask<bool> ReplaceAsync(
        HttpContext context,
        string payload,
        string? contentType = null
    ) => ReplaceAsync(context, Encoding.UTF8.GetBytes(payload), contentType);
}
