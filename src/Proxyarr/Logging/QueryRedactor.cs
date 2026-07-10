using System.Text;

namespace Proxyarr.Logging;

/// <summary>
/// Formats a request's query string for log output with credential-bearing values masked
/// (SABnzbd carries its API key in the query string on every call).
/// </summary>
public static class QueryRedactor
{
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "apikey",
        "api_key",
        "nzbkey",
        "ma_username",
        "ma_password",
        "username",
        "password",
    };

    public static string Redact(HttpRequest request)
    {
        if (request.Query.Count == 0)
        {
            return "";
        }

        var builder = new StringBuilder("?");
        var first = true;
        foreach (var (key, values) in request.Query)
        {
            foreach (var value in values)
            {
                if (!first)
                {
                    builder.Append('&');
                }

                first = false;
                builder.Append(key).Append('=');
                builder.Append(SensitiveKeys.Contains(key) ? "REDACTED" : value);
            }
        }

        return builder.ToString();
    }
}
