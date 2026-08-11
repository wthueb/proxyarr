using System.Net;
using System.Text.Json;
using Proxyarr.Configuration;

namespace Proxyarr.Clients.Sabnzbd;

/// <summary>
/// Makes SABnzbd <c>/api</c> side-calls on behalf of an in-flight proxied request, reusing that
/// request's <c>apikey</c> (the proxy stores no credentials). Created per request by
/// <see cref="SabnzbdApiClientFactory"/>.
/// </summary>
public sealed class SabnzbdApiClient(HttpClient http, string upstream, string apiKey)
{
    public const string HttpClientName = "sabnzbd-sidecall";

    /// <summary>
    /// Forwards a request to <c>/api?{query}</c> verbatim (the caller assembles the query, preserving
    /// the original request's parameters), returning the raw status and body.
    /// </summary>
    public async Task<(HttpStatusCode Status, string Body)> SendAsync(
        HttpMethod method,
        string query,
        HttpContent? content,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(method, $"{upstream}/api?{query}")
        {
            Content = content,
        };
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return (response.StatusCode, body);
    }

    /// <summary>True when the job still exists upstream (queue or history).</summary>
    public async Task<bool> JobIsLiveAsync(string nzoId, CancellationToken cancellationToken)
    {
        return await ExistsInAsync("queue", nzoId, cancellationToken)
            || await ExistsInAsync("history", nzoId, cancellationToken);
    }

    public static IReadOnlyList<string> ParseNzoIds(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (
                json.RootElement.TryGetProperty("nzo_ids", out var ids)
                && ids.ValueKind == JsonValueKind.Array
            )
            {
                return ids.EnumerateArray()
                    .Select(id => id.GetString())
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Select(id => id!)
                    .ToList();
            }
        }
        catch (JsonException)
        {
            // Fall through to empty.
        }

        return [];
    }

    private async Task<bool> ExistsInAsync(
        string mode,
        string nzoId,
        CancellationToken cancellationToken
    )
    {
        var query =
            $"mode={mode}&nzo_ids={Uri.EscapeDataString(nzoId)}&output=json"
            + $"&apikey={Uri.EscapeDataString(apiKey)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{upstream}/api?{query}");
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty(mode, out var container))
            {
                return false;
            }

            if (
                !container.TryGetProperty("slots", out var slots)
                || slots.ValueKind != JsonValueKind.Array
            )
            {
                return false;
            }

            return slots
                .EnumerateArray()
                .Any(slot => slot.TryGetProperty("nzo_id", out var id) && id.GetString() == nzoId);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>Builds a <see cref="SabnzbdApiClient"/> for one request, wiring in its apikey.</summary>
public sealed class SabnzbdApiClientFactory(IHttpClientFactory httpClientFactory)
{
    public SabnzbdApiClient Create(ClientInstanceConfig instance, HttpRequest request)
    {
        var http = httpClientFactory.CreateClient(SabnzbdApiClient.HttpClientName);
        var apiKey = request.Query["apikey"].ToString();
        return new SabnzbdApiClient(http, instance.Upstream, apiKey);
    }
}
