using Proxyarr.Configuration;

namespace Proxyarr.Logging;

public static class RequestLogScope
{
    /// <summary>Adds request metadata to every log emitted while the request is executing.</summary>
    public static WebApplication UseRequestLogScope(this WebApplication app, ProxyConfig config)
    {
        var instances = config.ResolvedClients.ToDictionary(
            instance => $"/{instance.Type}/{instance.Name}",
            instance => instance.Name,
            StringComparer.OrdinalIgnoreCase
        );

        app.Use(
            async (context, next) =>
            {
                var fields = new List<KeyValuePair<string, object?>>(4);
                if (FindInstance(context.Request.Path, instances) is { } instance)
                {
                    fields.Add(new("Instance", instance));
                }

                fields.Add(new("Method", context.Request.Method));
                fields.Add(new("Path", context.Request.Path.Value));
                fields.Add(new("Query", QueryRedactor.Redact(context.Request)));

                using (app.Logger.BeginScope(fields))
                {
                    await next();
                }
            }
        );

        return app;
    }

    private static string? FindInstance(
        PathString path,
        IReadOnlyDictionary<string, string> instances
    )
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value) || value[0] != '/')
        {
            return null;
        }

        var typeSeparator = value.IndexOf('/', 1);
        if (typeSeparator < 0)
        {
            return null;
        }

        var nameSeparator = value.IndexOf('/', typeSeparator + 1);
        var routeBase = value[..(nameSeparator < 0 ? value.Length : nameSeparator)];
        return instances.TryGetValue(routeBase, out var instance) ? instance : null;
    }
}
