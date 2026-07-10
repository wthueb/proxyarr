using System.Diagnostics;
using Proxyarr.Clients.QBittorrent;
using Proxyarr.Clients.Sabnzbd;
using Proxyarr.Configuration;
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

        foreach (var route in adapter.Routes)
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

            // Routes without transforms share the instance's prefix-only transformer.
            var transformer = route is { TransformRequest: null, TransformResponse: null }
                ? passThroughTransformer
                : new PrefixStripTransformer(
                    prefix,
                    route.TransformRequest,
                    route.TransformResponse
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
                            requestLogger.LogWarning(
                                "Rejected {Instance} {Method} {Path}{Query}: short-circuited by the OnRequest hook",
                                instance.Name,
                                context.Request.Method,
                                context.Request.Path.Value,
                                QueryRedactor.Redact(context.Request)
                            );
                            await shortCircuit.ExecuteAsync(context);
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
            adapter.Routes.Count,
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
