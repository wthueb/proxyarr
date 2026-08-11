using Proxyarr.Configuration;

namespace Proxyarr.Clients.QBittorrent;

/// <summary>
/// qBittorrent Web API v2 as shipped in qBittorrent 5.2.3 (Web API 2.15.x). Only Web API v2 is
/// supported; the v1 API (qBittorrent &lt; 4.1) is not proxied, and neither are the pre-5.0
/// endpoint names (torrents/pause, torrents/resume).
///
/// The route list mirrors exactly what Radarr's client uses, see
/// src/NzbDrone.Core/Download/Clients/QBittorrent/QBittorrentProxyV2.cs in Radarr. Instances with
/// dedupe enabled get category→tag hooks on top of the same endpoint surface.
/// </summary>
public sealed class QBittorrentAdapter(
    QBittorrentDedupe dedupe,
    QBittorrentPathRewriter pathRewriter
) : IDownloadClientAdapter
{
    /// <summary>
    /// The endpoint pattern/method surface, independent of dedupe. Pass-through and dedupe route
    /// variants share this list of paths; the pass-through theory test iterates it.
    /// </summary>
    public static readonly IReadOnlyList<ProxyRoute> PassThroughRoutes =
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

    public string Type => "qbittorrent";

    public IReadOnlyList<ProxyRoute> GetRoutes(ClientInstanceConfig instance)
    {
        if (instance.DedupeEnabled)
        {
            return DedupeRoutes(instance);
        }

        return QBittorrentPathRewriter.Enabled(instance)
            ? PathMappedPassThroughRoutes()
            : PassThroughRoutes;
    }

    private IReadOnlyList<ProxyRoute> PathMappedPassThroughRoutes() =>
        PassThroughRoutes
            .Select(route =>
                route.Pattern switch
                {
                    "/api/v2/app/preferences" => route with
                    {
                        TransformRequest = pathRewriter.StripAcceptEncodingAsync,
                        TransformResponse = pathRewriter.TransformPreferencesResponseAsync,
                    },
                    "/api/v2/torrents/info" => route with
                    {
                        TransformRequest = pathRewriter.StripAcceptEncodingAsync,
                        TransformResponse = pathRewriter.TransformInfoResponseAsync,
                    },
                    "/api/v2/torrents/properties" => route with
                    {
                        TransformRequest = pathRewriter.StripAcceptEncodingAsync,
                        TransformResponse = pathRewriter.TransformPropertiesResponseAsync,
                    },
                    "/api/v2/torrents/categories" => route with
                    {
                        TransformRequest = pathRewriter.StripAcceptEncodingAsync,
                        TransformResponse = pathRewriter.TransformCategoriesResponseAsync,
                    },
                    _ => route,
                }
            )
            .ToList();

    private IReadOnlyList<ProxyRoute> DedupeRoutes(ClientInstanceConfig instance) =>
        [
            ProxyRoute.Post("/api/v2/auth/login"),
            ProxyRoute.Get("/api/v2/app/webapiVersion"),
            ProxyRoute.Get("/api/v2/app/version"),
            PathMappedRoute(
                ProxyRoute.Get("/api/v2/app/preferences"),
                instance,
                pathRewriter.TransformPreferencesResponseAsync
            ),
            new ProxyRoute(
                "/api/v2/torrents/info",
                ["GET"],
                TransformRequest: dedupe.TransformInfoRequestAsync,
                TransformResponse: dedupe.TransformInfoResponseAsync
            ),
            PathMappedRoute(
                ProxyRoute.Get("/api/v2/torrents/properties"),
                instance,
                pathRewriter.TransformPropertiesResponseAsync
            ),
            ProxyRoute.Get("/api/v2/torrents/files"),
            new ProxyRoute(
                "/api/v2/torrents/categories",
                ["GET"],
                TransformRequest: dedupe.StripAcceptEncodingAsync,
                TransformResponse: dedupe.TransformCategoriesResponseAsync
            ),
            new ProxyRoute("/api/v2/torrents/add", ["POST"], Handle: dedupe.HandleAddAsync),
            new ProxyRoute("/api/v2/torrents/delete", ["POST"], Handle: dedupe.HandleDeleteAsync),
            new ProxyRoute(
                "/api/v2/torrents/setCategory",
                ["POST"],
                Handle: dedupe.HandleSetCategoryAsync
            ),
            new ProxyRoute(
                "/api/v2/torrents/createCategory",
                ["POST"],
                Handle: dedupe.HandleCreateCategoryAsync
            ),
            new ProxyRoute(
                "/api/v2/torrents/setShareLimits",
                ["POST"],
                Handle: dedupe.HandleSetShareLimitsAsync
            ),
            ProxyRoute.Post("/api/v2/torrents/topPrio"),
            ProxyRoute.Post("/api/v2/torrents/setForceStart"),
        ];

    private ProxyRoute PathMappedRoute(
        ProxyRoute route,
        ClientInstanceConfig instance,
        Func<HttpContext, ClientInstanceConfig, HttpResponseMessage?, ValueTask<bool>> response
    ) =>
        QBittorrentPathRewriter.Enabled(instance)
            ? route with
            {
                TransformRequest = pathRewriter.StripAcceptEncodingAsync,
                TransformResponse = response,
            }
            : route;
}
