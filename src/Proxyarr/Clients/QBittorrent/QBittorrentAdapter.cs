namespace Proxyarr.Clients.QBittorrent;

/// <summary>
/// qBittorrent Web API v2 as shipped in qBittorrent 5.2.3 (Web API 2.15.x). Only Web API v2 is
/// supported; the v1 API (qBittorrent &lt; 4.1) is not proxied, and neither are the pre-5.0
/// endpoint names (torrents/pause, torrents/resume).
///
/// The route list mirrors exactly what Radarr's client uses, see
/// src/NzbDrone.Core/Download/Clients/QBittorrent/QBittorrentProxyV2.cs in Radarr.
/// </summary>
public sealed class QBittorrentAdapter : IDownloadClientAdapter
{
    public string Type => "qbittorrent";

    public IReadOnlyList<ProxyRoute> Routes { get; } =
    [
        ProxyRoute.Post("/api/v2/auth/login"),
        ProxyRoute.Get("/api/v2/app/webapiVersion"),
        ProxyRoute.Get("/api/v2/app/version"),
        ProxyRoute.Get("/api/v2/app/preferences"),
        ProxyRoute.Get("/api/v2/torrents/info"),
        ProxyRoute.Get("/api/v2/torrents/properties"),
        ProxyRoute.Get("/api/v2/torrents/files"),
        ProxyRoute.Get("/api/v2/torrents/categories"),
        ProxyRoute.Post("/api/v2/torrents/add"),
        ProxyRoute.Post("/api/v2/torrents/delete"),
        ProxyRoute.Post("/api/v2/torrents/setCategory"),
        ProxyRoute.Post("/api/v2/torrents/createCategory"),
        ProxyRoute.Post("/api/v2/torrents/setShareLimits"),
        ProxyRoute.Post("/api/v2/torrents/topPrio"),
        ProxyRoute.Post("/api/v2/torrents/setForceStart"),
    ];
}
