using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Proxyarr.Configuration;

namespace Proxyarr.Clients.QBittorrent;

/// <summary>Thrown when a qBittorrent side-call returns 401/403 — propagated so Radarr re-logins.</summary>
public sealed class QBittorrentAuthException(int statusCode)
    : Exception($"qBittorrent returned {statusCode} for a proxy side-call")
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>Thrown when a required qBittorrent side-call fails for a non-auth reason (mapped to 502).</summary>
public sealed class QBittorrentUpstreamException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// One torrent as returned by <c>torrents/info</c>, limited to the fields dedup needs. qBittorrent
/// reports <c>tags</c> as a comma-separated string and <c>seeding_time</c> in seconds while share
/// limits are in minutes.
/// </summary>
public sealed record TorrentInfo(
    string Hash,
    string Category,
    IReadOnlyList<string> Tags,
    double Ratio,
    long SeedingTime,
    double RatioLimit,
    long SeedingTimeLimit,
    long InactiveSeedingTimeLimit
)
{
    public ShareLimits ShareLimits => new(RatioLimit, SeedingTimeLimit, InactiveSeedingTimeLimit);
}

/// <summary>
/// Makes qBittorrent Web API v2 side-calls on behalf of an in-flight proxied request, reusing that
/// request's <c>SID</c> cookie verbatim (the proxy stores no credentials). Created per request by
/// <see cref="QBittorrentApiClientFactory"/>. The underlying <see cref="HttpClient"/> must have its
/// cookie jar disabled or the manual <c>Cookie</c> header is swallowed.
/// </summary>
public sealed class QBittorrentApiClient(HttpClient http, string upstream, string? cookie)
{
    /// <summary>Name of the DI-registered side-call <see cref="HttpClient"/> (cookie jar disabled).</summary>
    public const string HttpClientName = "qbittorrent-sidecall";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public async Task<IReadOnlyList<TorrentInfo>> GetTorrentsAsync(
        IEnumerable<string> hashes,
        CancellationToken cancellationToken
    )
    {
        var filter = string.Join('|', hashes);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{upstream}/api/v2/torrents/info?hashes={Uri.EscapeDataString(filter)}"
        );
        using var response = await SendAsync(request, cancellationToken);
        await ThrowIfFailed(response, "torrents/info", cancellationToken);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        List<TorrentInfoDto>? dtos;
        try
        {
            dtos = JsonSerializer.Deserialize<List<TorrentInfoDto>>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new QBittorrentUpstreamException("Could not parse torrents/info response", ex);
        }

        return dtos is null ? [] : dtos.Select(dto => dto.ToTorrentInfo()).ToList();
    }

    public async Task<GlobalShareLimits> GetPreferencesAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{upstream}/api/v2/app/preferences"
        );
        using var response = await SendAsync(request, cancellationToken);
        await ThrowIfFailed(response, "app/preferences", cancellationToken);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            var prefs = JsonSerializer.Deserialize<PreferencesDto>(json, JsonOptions);
            return prefs is null
                ? GlobalShareLimits.None
                : new GlobalShareLimits(
                    prefs.MaxRatioEnabled,
                    prefs.MaxRatio,
                    prefs.MaxSeedingTimeEnabled,
                    prefs.MaxSeedingTime
                );
        }
        catch (JsonException ex)
        {
            throw new QBittorrentUpstreamException("Could not parse app/preferences response", ex);
        }
    }

    /// <summary>Forwards a rebuilt <c>torrents/add</c> body. The caller inspects the raw response.</summary>
    public async Task<(HttpStatusCode Status, string Body)> AddTorrentAsync(
        HttpContent content,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{upstream}/api/v2/torrents/add"
        )
        {
            Content = content,
        };
        using var response = await SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return (response.StatusCode, body);
    }

    public Task AddTagsAsync(
        IEnumerable<string> hashes,
        IEnumerable<string> tags,
        CancellationToken cancellationToken
    ) => PostFormAsync("torrents/addTags", TagForm(hashes, tags), cancellationToken);

    public Task RemoveTagsAsync(
        IEnumerable<string> hashes,
        IEnumerable<string> tags,
        CancellationToken cancellationToken
    ) => PostFormAsync("torrents/removeTags", TagForm(hashes, tags), cancellationToken);

    public Task SetCategoryAsync(
        IEnumerable<string> hashes,
        string category,
        CancellationToken cancellationToken
    ) =>
        PostFormAsync(
            "torrents/setCategory",
            new Dictionary<string, string>
            {
                ["hashes"] = string.Join('|', hashes),
                ["category"] = category,
            },
            cancellationToken
        );

    public Task SetShareLimitsAsync(
        IEnumerable<string> hashes,
        ShareLimits limits,
        string shareLimitAction,
        CancellationToken cancellationToken
    ) =>
        PostFormAsync(
            "torrents/setShareLimits",
            new Dictionary<string, string>
            {
                ["hashes"] = string.Join('|', hashes),
                ["ratioLimit"] = FormatRatio(limits.RatioLimit ?? ShareLimits.Global),
                ["seedingTimeLimit"] = (
                    limits.SeedingTimeLimit ?? ShareLimits.GlobalTime
                ).ToString(),
                ["inactiveSeedingTimeLimit"] = (
                    limits.InactiveSeedingTimeLimit ?? ShareLimits.GlobalTime
                ).ToString(),
                ["shareLimitAction"] = shareLimitAction,
            },
            cancellationToken
        );

    public Task DeleteAsync(
        IEnumerable<string> hashes,
        bool deleteFiles,
        CancellationToken cancellationToken
    ) =>
        PostFormAsync(
            "torrents/delete",
            new Dictionary<string, string>
            {
                ["hashes"] = string.Join('|', hashes),
                ["deleteFiles"] = deleteFiles ? "true" : "false",
            },
            cancellationToken
        );

    /// <summary>Ensures a category exists upstream; qBittorrent's 409 ("already exists") is success.</summary>
    public async Task CreateCategoryAsync(string category, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{upstream}/api/v2/torrents/createCategory"
        )
        {
            Content = new FormUrlEncodedContent(
                new Dictionary<string, string> { ["category"] = category }
            ),
        };
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return;
        }

        await ThrowIfFailed(response, "torrents/createCategory", cancellationToken);
    }

    private async Task PostFormAsync(
        string path,
        Dictionary<string, string> form,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{upstream}/api/v2/{path}")
        {
            Content = new FormUrlEncodedContent(form),
        };
        using var response = await SendAsync(request, cancellationToken);
        await ThrowIfFailed(response, path, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        if (!string.IsNullOrEmpty(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        try
        {
            var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                var status = (int)response.StatusCode;
                response.Dispose();
                throw new QBittorrentAuthException(status);
            }

            return response;
        }
        catch (HttpRequestException ex)
        {
            throw new QBittorrentUpstreamException(
                $"qBittorrent side-call to {request.RequestUri} failed",
                ex
            );
        }
    }

    private static async Task ThrowIfFailed(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken
    )
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new QBittorrentUpstreamException(
            $"qBittorrent {operation} returned {(int)response.StatusCode}: {body}"
        );
    }

    private static Dictionary<string, string> TagForm(
        IEnumerable<string> hashes,
        IEnumerable<string> tags
    ) => new() { ["hashes"] = string.Join('|', hashes), ["tags"] = string.Join(',', tags) };

    private static string FormatRatio(double value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private sealed class TorrentInfoDto
    {
        public string Hash { get; set; } = "";
        public string Category { get; set; } = "";
        public string Tags { get; set; } = "";
        public double Ratio { get; set; }
        public long SeedingTime { get; set; }
        public double RatioLimit { get; set; } = -2;
        public long SeedingTimeLimit { get; set; } = -2;
        public long InactiveSeedingTimeLimit { get; set; } = -2;

        public TorrentInfo ToTorrentInfo() =>
            new(
                Hash,
                Category,
                Tags.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                ),
                Ratio,
                SeedingTime,
                RatioLimit,
                SeedingTimeLimit,
                InactiveSeedingTimeLimit
            );
    }

    private sealed class PreferencesDto
    {
        public bool MaxRatioEnabled { get; set; }
        public double MaxRatio { get; set; } = -1;
        public bool MaxSeedingTimeEnabled { get; set; }
        public long MaxSeedingTime { get; set; } = -1;
    }
}

/// <summary>Builds a <see cref="QBittorrentApiClient"/> for one request, wiring in its SID cookie.</summary>
public sealed class QBittorrentApiClientFactory(IHttpClientFactory httpClientFactory)
{
    public QBittorrentApiClient Create(ClientInstanceConfig instance, HttpRequest request)
    {
        var http = httpClientFactory.CreateClient(QBittorrentApiClient.HttpClientName);
        var cookie = request.Headers.Cookie.ToString();
        return new QBittorrentApiClient(http, instance.Upstream, cookie);
    }
}
