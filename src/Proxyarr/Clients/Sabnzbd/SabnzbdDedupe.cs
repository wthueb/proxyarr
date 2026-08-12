using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Proxyarr.Configuration;
using Proxyarr.Dedupe;
using Proxyarr.Dedupe.Db;
using Proxyarr.Forwarding;
using Proxyarr.Logging;

namespace Proxyarr.Clients.Sabnzbd;

/// <summary>
/// Cross-instance dedup for SABnzbd. A release grabbed by several *arr instances is added upstream
/// once; ownership is tracked as per-instance claims in SQLite (SABnzbd has no tag concept). The
/// NZB's content key (segment message-IDs) dedupes the same release across indexers. The upstream
/// job's files live until its last claim is removed.
/// </summary>
public sealed class SabnzbdDedupe(
    SabnzbdApiClientFactory apiFactory,
    SabnzbdPathRewriter pathRewriter,
    ClaimStore store,
    DedupeGroups groups,
    KeyedAsyncLock locks,
    ILogger<SabnzbdDedupe> logger
)
{
    private static readonly TimeSpan PruneGrace = TimeSpan.FromHours(1);

    // ---- request dispatch (fully local for addfile and delete) ----------------------------------

    public async ValueTask<IResult?> OnRequestAsync(
        HttpContext context,
        ClientInstanceConfig instance
    )
    {
        var mode = context.Request.Query["mode"].ToString();

        if (mode.Equals("addfile", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleAddfileAsync(context, instance);
        }

        if (
            (
                mode.Equals("queue", StringComparison.OrdinalIgnoreCase)
                || mode.Equals("history", StringComparison.OrdinalIgnoreCase)
            )
            && context
                .Request.Query["name"]
                .ToString()
                .Equals("delete", StringComparison.OrdinalIgnoreCase)
        )
        {
            return await HandleDeleteAsync(context, instance);
        }

        return null; // forward with the transform hooks
    }

    private async Task<IResult?> HandleAddfileAsync(
        HttpContext context,
        ClientInstanceConfig instance
    )
    {
        if (!context.Request.HasFormContentType)
        {
            return null; // not a recognizable upload — forward unchanged
        }

        var group = groups.For(instance)!;
        var ct = context.RequestAborted;
        var form = await context.Request.ReadFormAsync(ct);
        var file = form.Files.FirstOrDefault();
        if (file is null)
        {
            return null;
        }

        byte[] nzb;
        await using (var stream = file.OpenReadStream())
        using (var buffer = new MemoryStream())
        {
            await stream.CopyToAsync(buffer, ct);
            nzb = buffer.ToArray();
        }

        var contentKey = NzbContentKey.Compute(nzb);
        var category =
            (form["cat"].ToString() is { Length: > 0 } formCat)
                ? formCat
                : context.Request.Query["cat"].ToString();
        var api = apiFactory.Create(instance, context.Request);

        using var _ = await locks.AcquireAsync($"{group.Key}|{contentKey}", ct);

        var existing = await store.GetByContentKeyAsync(group.Key, contentKey, ct);
        if (existing is not null)
        {
            if (await api.JobIsLiveAsync(existing.NzoId, ct))
            {
                await store.AddClaimAsync(existing.Id, instance.Name, category, ct);
                logger.LogInformation("SABnzbd add deduplicated", ("NzoId", existing.NzoId));
                return SyntheticNzoIds([existing.NzoId]);
            }

            // The tracked job is gone upstream; drop the stale row and re-add.
            await store.DeleteJobAsync(existing.Id, ct);
        }

        var content = RebuildNzbContent(file, nzb);
        // Preserve every original query parameter (priority, pp, script, ...); only the category is
        // translated (or dropped when unset). The form we re-upload carries the NZB itself.
        var query = BuildForwardQuery(
            context.Request.Query,
            ("cat", instance.Dedupe?.Category),
            ("output", "json")
        );
        var (status, body) = await api.SendAsync(HttpMethod.Post, query, content, ct);
        if (!IsSuccess(status))
        {
            return Results.Content(body, "application/json", statusCode: (int)status);
        }

        var nzoIds = SabnzbdApiClient.ParseNzoIds(body);
        if (nzoIds.Count > 0)
        {
            await store.AddJobAsync(group.Key, contentKey, nzoIds[0], instance.Name, category, ct);
        }

        return Results.Content(body, "application/json", statusCode: (int)status);
    }

    private async Task<IResult> HandleDeleteAsync(
        HttpContext context,
        ClientInstanceConfig instance
    )
    {
        var group = groups.For(instance)!;
        var ct = context.RequestAborted;
        var value = context.Request.Query["value"].ToString();
        var api = apiFactory.Create(instance, context.Request);

        if (value.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("SABnzbd delete-all bypassed deduplication");
            var (status, body) = await api.SendAsync(
                HttpMethod.Get,
                BuildForwardQuery(context.Request.Query, ("output", "json")),
                null,
                ct
            );
            return Results.Content(body, "application/json", statusCode: (int)status);
        }

        var toDelete = new List<string>();
        foreach (
            var nzoId in value.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            using var _ = await locks.AcquireAsync($"{group.Key}|nzo:{nzoId}", ct);
            var remaining = await store.RemoveClaimAsync(group.Key, nzoId, instance.Name, ct);
            if (remaining == 0)
            {
                toDelete.Add(nzoId);
                var job = await store.GetByNzoIdAsync(group.Key, nzoId, ct);
                if (job is not null)
                {
                    await store.DeleteJobAsync(job.Id, ct);
                }
            }
            else
            {
                logger.LogInformation(
                    "SABnzbd claim released",
                    ("NzoId", nzoId),
                    ("Remaining", remaining)
                );
            }
        }

        if (toDelete.Count > 0)
        {
            // Forward one real delete for the ids that lost their last claim, keeping the request's
            // own del_files/name/apikey and just narrowing value to that subset.
            var query = BuildForwardQuery(
                context.Request.Query,
                ("value", string.Join(',', toDelete)),
                ("output", "json")
            );
            await api.SendAsync(HttpMethod.Get, query, null, ct);
            logger.LogInformation(
                "SABnzbd jobs deleted upstream",
                ("NzoIds", string.Join(',', toDelete))
            );
        }

        return SyntheticStatus(true);
    }

    // ---- response transforms --------------------------------------------------------------------

    public ValueTask TransformRequestAsync(
        HttpContext context,
        ClientInstanceConfig instance,
        HttpRequestMessage proxyRequest
    )
    {
        var mode = context.Request.Query["mode"].ToString();
        if (mode is "queue" or "history")
        {
            // *arr scopes listings with its configured category, which differs from the shared
            // category used upstream. Fetch without SABnzbd's category filter and let the response
            // transform below apply the authoritative per-instance claims. This also works when
            // SABnzbd normalizes an unconfigured group category to its default category.
            var uri = new UriBuilder(proxyRequest.RequestUri!);
            uri.Query = BuildForwardQuery(context.Request.Query, ("category", null));
            proxyRequest.RequestUri = uri.Uri;
        }

        if (
            mode is "queue" or "history" or "get_config" or "retry"
            || (mode == "fullstatus" && SabnzbdPathRewriter.Enabled(instance))
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

        return context.Request.Query["mode"].ToString() switch
        {
            "queue" => await RewriteListingAsync(context, instance, response, "queue", "cat"),
            "history" => await RewriteListingAsync(
                context,
                instance,
                response,
                "history",
                "category"
            ),
            "get_config" => await InjectCategoriesAsync(context, instance, response),
            "fullstatus" when SabnzbdPathRewriter.Enabled(instance) =>
                await pathRewriter.TransformResponseAsync(context, instance, response),
            "retry" => await UpdateRetryAsync(context, instance, response),
            _ => true,
        };
    }

    private async ValueTask<bool> RewriteListingAsync(
        HttpContext context,
        ClientInstanceConfig instance,
        HttpResponseMessage response,
        string container,
        string categoryField
    )
    {
        var group = groups.For(instance)!;
        var ct = context.RequestAborted;
        var json = await response.Content.ReadAsStringAsync(ct);
        if (
            TryParse(json) is not JsonObject root
            || root[container] is not JsonObject listing
            || listing["slots"] is not JsonArray slots
        )
        {
            return true;
        }

        var nzoIds = slots
            .OfType<JsonObject>()
            .Select(slot => slot["nzo_id"]?.GetValue<string>())
            .Where(id => id is not null)
            .Select(id => id!)
            .ToList();

        var claimed = await store.GetClaimedCategoriesByNzoIdAsync(
            group.Key,
            instance.Name,
            nzoIds,
            ct
        );

        // SABnzbd has only one category per job, so the shared upstream category cannot isolate
        // group members. Claims are the source of truth: expose only jobs owned by this instance.
        for (var index = slots.Count - 1; index >= 0; index--)
        {
            if (slots[index] is not JsonObject slot)
            {
                slots.RemoveAt(index);
                continue;
            }

            if (
                slot["nzo_id"]?.GetValue<string>() is { } id
                && claimed.TryGetValue(id, out var category)
            )
            {
                slot[categoryField] = category;
                continue;
            }

            slots.RemoveAt(index);
        }

        RewriteSlotCount(listing, slots.Count);

        pathRewriter.RewriteListing(root, container, instance);

        await store.ReconcileAsync(group.Key, nzoIds, PruneGrace, ct);
        return await ResponseBody.ReplaceAsync(context, root.ToJsonString(), "application/json");
    }

    private static void RewriteSlotCount(JsonObject listing, int count)
    {
        if (listing["noofslots"] is not JsonValue current)
        {
            return;
        }

        // SABnzbd versions have emitted this field as both a JSON number and a numeric string.
        listing["noofslots"] = current.TryGetValue<string>(out _) ? count.ToString() : count;
    }

    private async ValueTask<bool> InjectCategoriesAsync(
        HttpContext context,
        ClientInstanceConfig instance,
        HttpResponseMessage response
    )
    {
        var group = groups.For(instance)!;
        var ct = context.RequestAborted;
        var inject = (instance.Dedupe?.AnnounceCategories ?? [])
            .Concat(await store.GetClaimedCategoryNamesAsync(group.Key, instance.Name, ct))
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (inject.Count == 0 && !SabnzbdPathRewriter.Enabled(instance))
        {
            return true;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        if (TryParse(json) is not JsonObject root || root["config"] is not JsonObject config)
        {
            return true;
        }

        var categories = config["categories"] as JsonArray;
        if (categories is null && inject.Count > 0)
        {
            categories = [];
            config["categories"] = categories;
        }

        var existing = (categories ?? [])
            .OfType<JsonObject>()
            .Select(category => category["name"]?.GetValue<string>())
            .Where(name => name is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in inject.Where(name => !existing.Contains(name)))
        {
            categories!.Add(new JsonObject { ["name"] = name });
        }

        pathRewriter.RewriteConfig(root, instance);

        return await ResponseBody.ReplaceAsync(context, root.ToJsonString(), "application/json");
    }

    private async ValueTask<bool> UpdateRetryAsync(
        HttpContext context,
        ClientInstanceConfig instance,
        HttpResponseMessage response
    )
    {
        var group = groups.For(instance)!;
        var ct = context.RequestAborted;
        var oldNzoId = context.Request.Query["value"].ToString();
        var json = await response.Content.ReadAsStringAsync(ct);

        if (TryParse(json) is JsonNode node)
        {
            var newNzoId =
                SabnzbdApiClient.ParseNzoIds(json).FirstOrDefault()
                ?? node["nzo_id"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(newNzoId) && oldNzoId.Length > 0 && newNzoId != oldNzoId)
            {
                await store.UpdateNzoIdAsync(group.Key, oldNzoId, newNzoId, ct);
            }
        }

        // Re-emit the body verbatim rather than returning true — the content was already read here.
        return await ResponseBody.ReplaceAsync(context, json, "application/json");
    }

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>
    /// Rebuilds a query string from the original request, applying overrides: an override with a
    /// value replaces/sets that parameter, an override with a null value drops it entirely (used to
    /// strip <c>cat</c> when no dedupe category is configured).
    /// </summary>
    private static string BuildForwardQuery(
        IQueryCollection query,
        params (string Key, string? Value)[] overrides
    )
    {
        var overridden = new HashSet<string>(
            overrides.Select(o => o.Key),
            StringComparer.OrdinalIgnoreCase
        );
        var pairs = new List<string>();

        foreach (var (key, values) in query)
        {
            if (overridden.Contains(key))
            {
                continue;
            }

            foreach (var value in values)
            {
                pairs.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value ?? "")}");
            }
        }

        foreach (var (key, value) in overrides)
        {
            if (value is not null)
            {
                pairs.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
            }
        }

        return string.Join('&', pairs);
    }

    private static HttpContent RebuildNzbContent(IFormFile file, byte[] bytes)
    {
        var part = new ByteArrayContent(bytes);
        if (!string.IsNullOrEmpty(file.ContentType))
        {
            part.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
        }

        return new MultipartFormDataContent { { part, file.Name, file.FileName } };
    }

    private static IResult SyntheticNzoIds(IReadOnlyList<string> nzoIds)
    {
        var ids = string.Join(',', nzoIds.Select(id => JsonSerializer.Serialize(id)));
        return Results.Content($$"""{"status":true,"nzo_ids":[{{ids}}]}""", "application/json");
    }

    private static IResult SyntheticStatus(bool status) =>
        Results.Content($$"""{"status":{{(status ? "true" : "false")}}}""", "application/json");

    private static bool IsSuccess(HttpStatusCode status) =>
        status is >= (HttpStatusCode)200 and < (HttpStatusCode)300;

    private static JsonNode? TryParse(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
