namespace Proxyarr.Clients.Sabnzbd;

/// <summary>
/// SABnzbd API as shipped in SABnzbd 5.0.4. SABnzbd exposes a single <c>/api</c> endpoint
/// dispatched on the <c>mode</c> query parameter, so the allow-list here is a set of modes rather
/// than paths. The modes mirror exactly what Radarr's client uses, see
/// src/NzbDrone.Core/Download/Clients/Sabnzbd/SabnzbdProxy.cs in Radarr.
/// </summary>
public sealed class SabnzbdAdapter : IDownloadClientAdapter
{
    /// <remarks>
    /// Deliberately excludes destructive/administrative modes (<c>shutdown</c>, <c>set_config</c>,
    /// <c>restart</c>, ...) — extend only with modes a download client integration needs.
    /// </remarks>
    public static readonly IReadOnlySet<string> AllowedModes = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "addfile",
        "version",
        "get_config",
        "fullstatus",
        "queue",
        "history",
        "retry",
    };

    public string Type => "sabnzbd";

    public IReadOnlyList<ProxyRoute> Routes { get; } = [new("/api", ["GET", "POST"], ValidateMode)];

    private static IResult? ValidateMode(HttpRequest request)
    {
        string mode = request.Query["mode"].ToString();

        if (string.IsNullOrEmpty(mode))
        {
            return Rejection("The 'mode' query parameter is required");
        }

        if (!AllowedModes.Contains(mode))
        {
            return Rejection($"API mode '{mode}' is not proxied");
        }

        return null;
    }

    /// <summary>Mimics SABnzbd's own error shape so callers can parse it uniformly.</summary>
    private static IResult Rejection(string error) =>
        Results.Json(new { status = false, error }, statusCode: StatusCodes.Status400BadRequest);
}
