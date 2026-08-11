using System.Text.Json;
using System.Text.Json.Nodes;
using Proxyarr.Configuration;
using Proxyarr.Forwarding;

namespace Proxyarr.Clients.QBittorrent;

/// <summary>Rewrites path-bearing qBittorrent JSON fields into an upstream's reported namespace.</summary>
public sealed class QBittorrentPathRewriter
{
    public static bool Enabled(ClientInstanceConfig instance) => instance.PathMappings.Count > 0;

    public ValueTask StripAcceptEncodingAsync(
        HttpContext context,
        ClientInstanceConfig instance,
        HttpRequestMessage proxyRequest
    )
    {
        proxyRequest.Headers.Remove("Accept-Encoding");
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> TransformPreferencesResponseAsync(
        HttpContext context,
        ClientInstanceConfig instance,
        HttpResponseMessage? response
    ) => RewriteObjectResponseAsync(context, instance, response, RewritePreferences);

    public ValueTask<bool> TransformInfoResponseAsync(
        HttpContext context,
        ClientInstanceConfig instance,
        HttpResponseMessage? response
    ) => RewriteArrayResponseAsync(context, instance, response, RewriteInfo);

    public ValueTask<bool> TransformPropertiesResponseAsync(
        HttpContext context,
        ClientInstanceConfig instance,
        HttpResponseMessage? response
    ) => RewriteObjectResponseAsync(context, instance, response, RewriteProperties);

    public ValueTask<bool> TransformCategoriesResponseAsync(
        HttpContext context,
        ClientInstanceConfig instance,
        HttpResponseMessage? response
    ) => RewriteObjectResponseAsync(context, instance, response, RewriteCategories);

    public bool RewritePreferences(JsonObject preferences, ClientInstanceConfig instance) =>
        RewriteProperty(preferences, "save_path", instance);

    public bool RewriteInfo(JsonArray torrents, ClientInstanceConfig instance)
    {
        var changed = false;
        foreach (var torrent in torrents.OfType<JsonObject>())
        {
            changed |= RewriteProperty(torrent, "content_path", instance);
            changed |= RewriteProperty(torrent, "save_path", instance);
        }

        return changed;
    }

    public bool RewriteProperties(JsonObject properties, ClientInstanceConfig instance) =>
        RewriteProperty(properties, "save_path", instance);

    public bool RewriteCategories(JsonObject categories, ClientInstanceConfig instance)
    {
        var changed = false;
        foreach (var category in categories.Select(entry => entry.Value).OfType<JsonObject>())
        {
            changed |= RewriteProperty(category, "savePath", instance);
            changed |= RewriteProperty(category, "save_path", instance);
        }

        return changed;
    }

    private static bool RewriteProperty(
        JsonObject parent,
        string property,
        ClientInstanceConfig instance
    )
    {
        if (
            parent[property] is not JsonValue value
            || !value.TryGetValue<string>(out var path)
            || string.IsNullOrEmpty(path)
        )
        {
            return false;
        }

        var rewritten = ReportedPathMapper.Rewrite(path, instance.PathMappings);
        if (rewritten == path)
        {
            return false;
        }

        parent[property] = rewritten;
        return true;
    }

    private static async ValueTask<bool> RewriteObjectResponseAsync(
        HttpContext context,
        ClientInstanceConfig instance,
        HttpResponseMessage? response,
        Func<JsonObject, ClientInstanceConfig, bool> rewrite
    )
    {
        if (response is null || !response.IsSuccessStatusCode)
        {
            return true;
        }

        var json = await response.Content.ReadAsStringAsync(context.RequestAborted);
        try
        {
            if (JsonNode.Parse(json) is JsonObject root)
            {
                rewrite(root, instance);
                json = root.ToJsonString();
            }
        }
        catch (JsonException) { }

        return await ResponseBody.ReplaceAsync(context, json, "application/json");
    }

    private static async ValueTask<bool> RewriteArrayResponseAsync(
        HttpContext context,
        ClientInstanceConfig instance,
        HttpResponseMessage? response,
        Func<JsonArray, ClientInstanceConfig, bool> rewrite
    )
    {
        if (response is null || !response.IsSuccessStatusCode)
        {
            return true;
        }

        var json = await response.Content.ReadAsStringAsync(context.RequestAborted);
        try
        {
            if (JsonNode.Parse(json) is JsonArray root)
            {
                rewrite(root, instance);
                json = root.ToJsonString();
            }
        }
        catch (JsonException) { }

        return await ResponseBody.ReplaceAsync(context, json, "application/json");
    }
}
