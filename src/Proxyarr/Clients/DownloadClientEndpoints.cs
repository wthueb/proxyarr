using System.Diagnostics;
using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Proxyarr.Clients.QBittorrent;
using Proxyarr.Clients.Sabnzbd;
using Proxyarr.Configuration;
using Proxyarr.Dedupe;
using Proxyarr.Dedupe.Db;
using Proxyarr.Forwarding;
using Proxyarr.Logging;

namespace Proxyarr.Clients;

public static class DownloadClientEndpoints
{
    public static IServiceCollection AddDownloadClients(this IServiceCollection services)
    {
        services.AddHttpForwarder();
        // For adapters whose routes handle requests locally (ProxyRoute.Handle) and need to make
        // their own upstream calls via an injected IHttpClientFactory.
        services.AddHttpClient();
        services.AddSingleton<UpstreamForwarder>();

        // Dedup infrastructure. DedupeGroups is derived from the (already-registered) ProxyConfig;
        // the keyed lock serializes concurrent adds/deletes of the same item within a group.
        services.AddSingleton(provider =>
            DedupeGroups.Build(provider.GetRequiredService<ProxyConfig>())
        );
        services.AddSingleton<KeyedAsyncLock>();

        // qBittorrent dedup side-calls reuse the incoming SID cookie, so the client's own cookie jar
        // must be disabled or it swallows the manually attached Cookie header.
        services
            .AddHttpClient(QBittorrentApiClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() =>
                new SocketsHttpHandler
                {
                    UseProxy = false,
                    AllowAutoRedirect = false,
                    AutomaticDecompression = DecompressionMethods.None,
                    UseCookies = false,
                }
            );
        services.AddSingleton<QBittorrentApiClientFactory>();
        services.AddSingleton<QBittorrentDedupe>();

        // SABnzbd dedup: SQLite-backed claim store (context per operation via the factory) plus the
        // apikey-reusing side-call client. The DB path is defaulted in Program before this runs.
        services.AddDbContextFactory<ProxyarrDbContext>(
            (provider, options) =>
            {
                var config = provider.GetRequiredService<ProxyConfig>();
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = config.Database,
                }.ToString();
                options.UseSqlite(connectionString);
                options.AddInterceptors(new SqlitePragmaInterceptor());
            }
        );
        services.AddSingleton<ClaimStore>();
        services
            .AddHttpClient(SabnzbdApiClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() =>
                new SocketsHttpHandler
                {
                    UseProxy = false,
                    AllowAutoRedirect = false,
                    AutomaticDecompression = DecompressionMethods.None,
                }
            );
        services.AddSingleton<SabnzbdApiClientFactory>();
        services.AddSingleton<SabnzbdDedupe>();

        services.AddSingleton<IDownloadClientAdapter, QBittorrentAdapter>();
        services.AddSingleton<IDownloadClientAdapter, SabnzbdAdapter>();
        return services;
    }

    /// <summary>
    /// Maps every configured client instance under its <c>/{name}</c> prefix, exposing only the
    /// routes its adapter declares.
    /// </summary>
    public static WebApplication MapDownloadClients(this WebApplication app)
    {
        var config = app.Services.GetRequiredService<ProxyConfig>();
        var adapters = app
            .Services.GetServices<IDownloadClientAdapter>()
            .ToDictionary(adapter => adapter.Type, StringComparer.OrdinalIgnoreCase);
        var requestLogger = app
            .Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Proxyarr.Requests");

        // Initialize the SABnzbd claim store up front (runs migrations) so a bad database path fails
        // at startup rather than on the first NZB add.
        if (
            config.Clients.Any(instance =>
                instance.Type.Equals("sabnzbd", StringComparison.OrdinalIgnoreCase)
                && instance.DedupeEnabled
            )
        )
        {
            app.Services.GetRequiredService<ClaimStore>()
                .InitializeAsync()
                .GetAwaiter()
                .GetResult();
        }

        foreach (var instance in config.Clients)
        {
            if (!adapters.TryGetValue(instance.Type, out var adapter))
            {
                throw new ConfigurationException(
                    $"Client '{instance.Name}' has unknown type '{instance.Type}'. "
                        + $"Known types: {string.Join(", ", adapters.Keys.Order())}."
                );
            }

            MapInstance(app, adapter, instance, requestLogger);
        }

        // Surfaces the exact request when Radarr (or anything else) starts calling an endpoint
        // that no adapter declares yet — the first thing to check when extending an adapter.
        // A middleware (not a fallback route) so it can't shadow routing's own 405 handling, and
        // the forwarded-marker check keeps upstream 404s (e.g. an unknown torrent hash) out.
        app.Use(
            async (context, next) =>
            {
                await next();

                if (
                    context.Response.StatusCode
                        is StatusCodes.Status404NotFound
                            or StatusCodes.Status405MethodNotAllowed
                    && !context.Items.ContainsKey(UpstreamForwarder.ForwardedItemKey)
                )
                {
                    requestLogger.LogWarning(
                        "No proxied endpoint matches {Method} {Path}{Query} (responding {StatusCode})",
                        context.Request.Method,
                        context.Request.Path.Value,
                        QueryRedactor.Redact(context.Request),
                        context.Response.StatusCode
                    );
                }
            }
        );

        return app;
    }

    private static void MapInstance(
        WebApplication app,
        IDownloadClientAdapter adapter,
        ClientInstanceConfig instance,
        ILogger requestLogger
    )
    {
        var prefix = new PathString($"/{instance.Name}");
        var passThroughTransformer = new PrefixStripTransformer(prefix);
        var routes = adapter.GetRoutes(instance);

        foreach (var route in routes)
        {
            var pattern = $"/{instance.Name}{route.Pattern}";

            if (route.Handle is { } handle)
            {
                app.MapMethods(
                    pattern,
                    route.Methods,
                    (HttpContext context) =>
                        HandleLocallyAsync(context, instance, handle, requestLogger)
                );
                continue;
            }

            // Routes without transforms share the instance's prefix-only transformer. Otherwise the
            // instance is curried into the transform delegates here so PrefixStripTransformer keeps
            // its instance-agnostic signature.
            var transformer = route is { TransformRequest: null, TransformResponse: null }
                ? passThroughTransformer
                : new PrefixStripTransformer(
                    prefix,
                    route.TransformRequest is { } transformRequest
                        ? (context, proxyRequest) =>
                            transformRequest(context, instance, proxyRequest)
                        : null,
                    route.TransformResponse is { } transformResponse
                        ? (context, proxyResponse) =>
                            transformResponse(context, instance, proxyResponse)
                        : null
                );

            app.MapMethods(
                pattern,
                route.Methods,
                async (HttpContext context, UpstreamForwarder forwarder) =>
                {
                    if (route.Validate?.Invoke(context.Request) is { } rejection)
                    {
                        requestLogger.LogWarning(
                            "Rejected {Instance} {Method} {Path}{Query}: refused by the endpoint guard",
                            instance.Name,
                            context.Request.Method,
                            context.Request.Path.Value,
                            QueryRedactor.Redact(context.Request)
                        );
                        await rejection.ExecuteAsync(context);
                        return;
                    }

                    if (route.OnRequest is { } onRequest)
                    {
                        context.Request.EnableBuffering();

                        if (await onRequest(context, instance) is { } shortCircuit)
                        {
                            // Not a rejection: an OnRequest short-circuit is normal operation
                            // (e.g. a SABnzbd dedup hit answered locally without an upstream call).
                            await shortCircuit.ExecuteAsync(context);
                            requestLogger.LogInformation(
                                "Handled {Instance} {Method} {Path}{Query} -> {StatusCode} (short-circuited by the OnRequest hook)",
                                instance.Name,
                                context.Request.Method,
                                context.Request.Path.Value,
                                QueryRedactor.Redact(context.Request),
                                context.Response.StatusCode
                            );
                            return;
                        }

                        if (context.Request.Body.CanSeek)
                        {
                            context.Request.Body.Position = 0;
                        }
                    }

                    await forwarder.ForwardAsync(context, instance, transformer);
                }
            );
        }

        app.Logger.LogInformation(
            "Proxying /{Name} ({Type}, {RouteCount} endpoints) -> {Upstream}",
            instance.Name,
            adapter.Type,
            routes.Count,
            instance.Upstream
        );
    }

    /// <summary>
    /// Runs a route's <see cref="ProxyRoute.Handle"/> hook, keeping the invariant that every
    /// request produces exactly one outcome log line (forwarded requests get theirs from
    /// <see cref="UpstreamForwarder"/>).
    /// </summary>
    private static async Task HandleLocallyAsync(
        HttpContext context,
        ClientInstanceConfig instance,
        Func<HttpContext, ClientInstanceConfig, Task<IResult>> handle,
        ILogger requestLogger
    )
    {
        // A locally handled route is a declared endpoint, so its responses (including 404s) must
        // not trip the unmatched-endpoint warning middleware.
        context.Items[UpstreamForwarder.ForwardedItemKey] = true;

        var startTimestamp = Stopwatch.GetTimestamp();
        var result = await handle(context, instance);
        await result.ExecuteAsync(context);
        var elapsedMs = Math.Round(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds, 1);

        requestLogger.LogInformation(
            "Handled {Instance} {Method} {Path}{Query} -> {StatusCode} in {ElapsedMs}ms",
            instance.Name,
            context.Request.Method,
            context.Request.Path.Value,
            QueryRedactor.Redact(context.Request),
            context.Response.StatusCode,
            elapsedMs
        );
    }
}
