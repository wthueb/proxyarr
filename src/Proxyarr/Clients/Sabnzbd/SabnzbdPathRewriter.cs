using System.Text.Json;
using System.Text.Json.Nodes;
using Proxyarr.Configuration;
using Proxyarr.Forwarding;

namespace Proxyarr.Clients.Sabnzbd;

/// <summary>Rewrites path-bearing SABnzbd JSON fields into an upstream's reported namespace.</summary>
public sealed class SabnzbdPathRewriter
{
    public static bool Enabled(ClientInstanceConfig instance) => instance.PathMappings.Count > 0;

    public ValueTask StripAcceptEncodingAsync(
        HttpContext context,
        ClientInstanceConfig instance,
        HttpRequestMessage proxyRequest
    )
    {
        if (
            context.Request.Query["mode"].ToString()
            is "queue"
                or "history"
                or "get_config"
                or "fullstatus"
        )
        {
            proxyRequest.Headers.Remove("Accept-Encoding");
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<bool> TransformResponseAsync(
        HttpContext context,
        ClientInstanceConfig instance,
        HttpResponseMessage? response
    )
    {
        if (response is null || !response.IsSuccessStatusCode)
        {
            return true;
        }

        var mode = context.Request.Query["mode"].ToString();
        if (mode is not ("queue" or "history" or "get_config" or "fullstatus"))
        {
            return true;
        }

        var json = await response.Content.ReadAsStringAsync(context.RequestAborted);
        try
        {
            if (JsonNode.Parse(json) is JsonObject root)
            {
                RewriteForMode(root, mode, instance);
                json = root.ToJsonString();
            }
        }
        catch (JsonException) { }

        return await ResponseBody.ReplaceAsync(context, json, "application/json");
    }

    public bool RewriteForMode(JsonObject root, string mode, ClientInstanceConfig instance) =>
        mode switch
        {
            "queue" => RewriteListing(root, "queue", instance),
            "history" => RewriteListing(root, "history", instance),
            "get_config" => RewriteConfig(root, instance),
            "fullstatus" => RewriteFullStatus(root, instance),
            _ => false,
        };

    public bool RewriteListing(JsonObject root, string container, ClientInstanceConfig instance)
    {
        if (root[container] is not JsonObject listing || listing["slots"] is not JsonArray slots)
        {
            return false;
        }

        var changed = false;
        foreach (var slot in slots.OfType<JsonObject>())
        {
            changed |= RewriteProperty(slot, "storage", instance);
        }

        return changed;
    }

    public bool RewriteConfig(JsonObject root, ClientInstanceConfig instance)
    {
        if (root["config"] is not JsonObject config)
        {
            return false;
        }

        var changed = false;
        if (config["misc"] is JsonObject misc)
        {
            changed |= RewriteProperty(misc, "complete_dir", instance);
            changed |= RewriteProperty(misc, "completedir", instance);
        }

        if (config["categories"] is JsonArray categories)
        {
            foreach (var category in categories.OfType<JsonObject>())
            {
                changed |= RewriteProperty(category, "dir", instance);
            }
        }

        return changed;
    }

    public bool RewriteFullStatus(JsonObject root, ClientInstanceConfig instance)
    {
        var changed = RewriteProperty(root, "complete_dir", instance);
        changed |= RewriteProperty(root, "completedir", instance);
        if (root["status"] is JsonObject status)
        {
            changed |= RewriteProperty(status, "complete_dir", instance);
            changed |= RewriteProperty(status, "completedir", instance);
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
}
