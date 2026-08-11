using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Proxyarr.Configuration;
using Proxyarr.Dedupe;
using Proxyarr.Forwarding;
using Proxyarr.Logging;

namespace Proxyarr.Clients.QBittorrent;

/// <summary>
/// Cross-instance dedup for qBittorrent. Categories become per-instance tags (a torrent can carry
/// many), so several *arr instances share one torrent that is downloaded once. qBittorrent itself is
/// the state store: the managed tags on a torrent are exactly the member instance names that still
/// want it. A torrent's files are never deleted while any managed tag remains; the last removal
/// either deletes now (if a seed limit is already surpassed) or hands cleanup to qBittorrent by
/// pinning its share-limit action to remove-with-content.
/// </summary>
public sealed class QBittorrentDedupe(
    QBittorrentApiClientFactory apiFactory,
    DedupeGroups groups,
    KeyedAsyncLock locks,
    ILogger<QBittorrentDedupe> logger
)
{
    private const string RequestedCategoryKey = "proxyarr.qbt.requestedCategory";

    private static readonly IResult OkResult = Results.Text("Ok.");

    // Categories Radarr believes exist, per instance, seeded from createCategory/add/info requests.
    // Injected into the categories response so Radarr's check→create→recheck flow self-heals across
    // restarts (this in-memory set is the only piece of qBt dedup state the proxy holds).
    private readonly ConcurrentDictionary<
        string,
        ConcurrentDictionary<string, byte>
    > _knownCategories = new(StringComparer.OrdinalIgnoreCase);

    // ---- torrents/add ---------------------------------------------------------------------------

    public Task<IResult> HandleAddAsync(HttpContext context, ClientInstanceConfig instance) =>
        GuardAsync(instance, "add", () => AddAsync(context, instance));

    private async Task<IResult> AddAsync(HttpContext context, ClientInstanceConfig instance)
    {
        var group = groups.For(instance)!;
        var ct = context.RequestAborted;
        var form = await context.Request.ReadFormAsync(ct);

        var requestedCategory = form["category"].ToString();
        RememberCategory(instance, requestedCategory);

        var files = await ReadFilesAsync(form, ct);
        var (hashes, anyUnparseable) = DeriveHashes(form, files);
        var api = apiFactory.Create(instance, context.Request);

        if (hashes.Count == 0)
        {
            // Plain http(s) URL or an unparseable .torrent: nothing to dedup on, so just forward the
            // rewritten form (category/tags translated) and relay the upstream result.
            if (anyUnparseable)
            {
                logger.LogWarning("qBittorrent add has no derivable info hash");
            }

            var (status, body) = await api.AddTorrentAsync(
                RebuildAddForm(form, files, instance),
                ct
            );
            return Results.Text(body, statusCode: (int)status);
        }

        var requestedLimits = ParseLimits(form);
        var lockKey = $"{group.Key}|{string.Join(',', hashes.Order(StringComparer.Ordinal))}";
        using var _ = await locks.AcquireAsync(lockKey, ct);

        var existing = await api.GetTorrentsAsync(hashes, ct);
        var allExist =
            hashes.Count > 0
            && hashes.All(hash =>
                existing.Any(t => t.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase))
            );

        if (allExist)
        {
            // Duplicate grab: adopt the existing torrent by tagging it and raising limits to the max.
            await api.AddTagsAsync(hashes, [instance.Name], ct);
            await RaiseShareLimitsAsync(api, existing, requestedLimits, ct);
            logger.LogInformation(
                "qBittorrent add deduplicated",
                ("Hashes", string.Join(',', hashes))
            );
            return OkResult;
        }

        var (addStatus, addBody) = await api.AddTorrentAsync(
            RebuildAddForm(form, files, instance),
            ct
        );
        if ((int)addStatus is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden)
        {
            return Results.Text(addBody, statusCode: (int)addStatus);
        }

        if (!IsAddSuccess(addStatus, addBody))
        {
            // Lost-race recovery: a sibling may have added it between our check and POST.
            var recheck = await api.GetTorrentsAsync(hashes, ct);
            if (
                hashes.All(hash =>
                    recheck.Any(t => t.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase))
                )
            )
            {
                await api.AddTagsAsync(hashes, [instance.Name], ct);
                await RaiseShareLimitsAsync(api, recheck, requestedLimits, ct);
                return OkResult;
            }

            return Results.Text(addBody, statusCode: (int)addStatus);
        }

        // The instance tag was applied at add time via the rebuilt form's `tags` field, so no
        // separate addTags call is needed here. Pin the share-limit action to Stop so no global
        // "when limit reached" setting can delete a tagged torrent out from under a sibling.
        await api.SetShareLimitsAsync(hashes, requestedLimits, ShareLimitAction.Stop, ct);
        return OkResult;
    }

    // ---- torrents/info --------------------------------------------------------------------------

    /// <summary>Rewrites <c>category=X</c> to <c>tag={instance}</c> and remembers X to echo it back.</summary>
    public ValueTask TransformInfoRequestAsync(
        HttpContext context,
        ClientInstanceConfig instance,
        HttpRequestMessage proxyRequest
    )
    {
        proxyRequest.Headers.Remove("Accept-Encoding");

        var category = context.Request.Query["category"].ToString();
        if (string.IsNullOrEmpty(category))
        {
            return ValueTask.CompletedTask;
        }

        RememberCategory(instance, category);
        context.Items[RequestedCategoryKey] = category;

        var uri = proxyRequest.RequestUri!;
        var parsed = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        parsed.Remove("category");
        parsed["tag"] = instance.Name;
        var pairs = parsed.SelectMany(kv =>
            kv.Value.Select(value =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(value ?? "")}"
            )
        );
        proxyRequest.RequestUri = new UriBuilder(uri) { Query = string.Join('&', pairs) }.Uri;
        return ValueTask.CompletedTask;
    }

    /// <summary>Sets each returned torrent's <c>category</c> to the one the requester filtered on.</summary>
    public async ValueTask<bool> TransformInfoResponseAsync(
        HttpContext context,
        ClientInstanceConfig instance,
        HttpResponseMessage? response
    )
    {
        if (
            response is null
            || !response.IsSuccessStatusCode
            || context.Items[RequestedCategoryKey] is not string category
        )
        {
            return true;
        }

        var json = await response.Content.ReadAsStringAsync(context.RequestAborted);
        if (TryParse(json) is not JsonArray array)
        {
            return true;
        }

        foreach (var item in array)
        {
            if (item is JsonObject torrent)
            {
                torrent["category"] = category;
            }
        }

        return await ResponseBody.ReplaceAsync(context, array.ToJsonString(), "application/json");
    }

    // ---- torrents/categories --------------------------------------------------------------------

    public ValueTask StripAcceptEncodingAsync(
        HttpContext context,
        ClientInstanceConfig instance,
        HttpRequestMessage proxyRequest
    )
    {
        proxyRequest.Headers.Remove("Accept-Encoding");
        return ValueTask.CompletedTask;
    }

    /// <summary>Injects the instance's remembered categories so Radarr's exists-check passes.</summary>
    public async ValueTask<bool> TransformCategoriesResponseAsync(
        HttpContext context,
        ClientInstanceConfig instance,
        HttpResponseMessage? response
    )
    {
        if (response is null || !response.IsSuccessStatusCode)
        {
            return true;
        }

        var remembered = KnownCategories(instance);
        if (remembered.IsEmpty)
        {
            return true;
        }

        var json = await response.Content.ReadAsStringAsync(context.RequestAborted);
        if (TryParse(json) is not JsonObject categories)
        {
            return true;
        }

        foreach (var name in remembered.Keys)
        {
            if (!categories.ContainsKey(name))
            {
                categories[name] = new JsonObject { ["name"] = name, ["savePath"] = "" };
            }
        }

        return await ResponseBody.ReplaceAsync(
            context,
            categories.ToJsonString(),
            "application/json"
        );
    }

    // ---- torrents/createCategory ----------------------------------------------------------------

    public Task<IResult> HandleCreateCategoryAsync(
        HttpContext context,
        ClientInstanceConfig instance
    ) => GuardAsync(instance, "createCategory", () => CreateCategoryAsync(context, instance));

    private async Task<IResult> CreateCategoryAsync(
        HttpContext context,
        ClientInstanceConfig instance
    )
    {
        var ct = context.RequestAborted;
        var form = await context.Request.ReadFormAsync(ct);
        RememberCategory(instance, form["category"].ToString());

        var dedupeCategory = instance.Dedupe?.Category;
        if (!string.IsNullOrEmpty(dedupeCategory))
        {
            var api = apiFactory.Create(instance, context.Request);
            await api.CreateCategoryAsync(dedupeCategory, ct);
        }

        return OkResult;
    }

    // ---- torrents/delete ------------------------------------------------------------------------

    public Task<IResult> HandleDeleteAsync(HttpContext context, ClientInstanceConfig instance) =>
        GuardAsync(instance, "delete", () => DeleteAsync(context, instance));

    private async Task<IResult> DeleteAsync(HttpContext context, ClientInstanceConfig instance)
    {
        var group = groups.For(instance)!;
        var ct = context.RequestAborted;
        var form = await context.Request.ReadFormAsync(ct);
        var hashesRaw = form["hashes"].ToString();
        var deleteFiles = form["deleteFiles"]
            .ToString()
            .Equals("true", StringComparison.OrdinalIgnoreCase);
        var api = apiFactory.Create(instance, context.Request);

        if (hashesRaw.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("qBittorrent delete-all bypassed deduplication");
            await api.DeleteAsync(["all"], deleteFiles, ct);
            return OkResult;
        }

        foreach (var hash in SplitHashes(hashesRaw))
        {
            using var _ = await locks.AcquireAsync($"{group.Key}|{hash.ToLowerInvariant()}", ct);
            await DeleteOneAsync(api, group, instance, hash, ct);
        }

        return OkResult;
    }

    private async Task DeleteOneAsync(
        QBittorrentApiClient api,
        DedupeGroup group,
        ClientInstanceConfig instance,
        string hash,
        CancellationToken ct
    )
    {
        var torrent = (await api.GetTorrentsAsync([hash], ct)).FirstOrDefault(t =>
            t.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase)
        );
        if (torrent is null)
        {
            return; // already gone upstream
        }

        await api.RemoveTagsAsync([hash], [instance.Name], ct);

        var remaining = torrent
            .Tags.Where(group.IsManagedTag)
            .Where(tag => !tag.Equals(instance.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (remaining.Count > 0)
        {
            logger.LogInformation(
                "qBittorrent tag removed",
                ("Hash", hash),
                ("Others", string.Join(',', remaining))
            );
            return;
        }

        // Last owner gone: never an immediate delete. Delete now only if a seed limit is already
        // surpassed; otherwise let qBittorrent remove-with-content when a limit is eventually hit.
        var global = NeedsGlobalPrefs(torrent.ShareLimits)
            ? await api.GetPreferencesAsync(ct)
            : GlobalShareLimits.None;

        if (torrent.ShareLimits.IsSurpassed(torrent.Ratio, torrent.SeedingTime, global))
        {
            await api.DeleteAsync([hash], deleteFiles: true, ct);
            logger.LogInformation("qBittorrent torrent deleted", ("Hash", hash));
        }
        else
        {
            await api.SetShareLimitsAsync(
                [hash],
                torrent.ShareLimits,
                ShareLimitAction.RemoveWithContent,
                ct
            );
            logger.LogInformation("qBittorrent torrent scheduled for deletion", ("Hash", hash));
        }
    }

    // ---- torrents/setShareLimits ----------------------------------------------------------------

    public Task<IResult> HandleSetShareLimitsAsync(
        HttpContext context,
        ClientInstanceConfig instance
    ) => GuardAsync(instance, "setShareLimits", () => SetShareLimitsAsync(context, instance));

    private async Task<IResult> SetShareLimitsAsync(
        HttpContext context,
        ClientInstanceConfig instance
    )
    {
        var group = groups.For(instance)!;
        var ct = context.RequestAborted;
        var form = await context.Request.ReadFormAsync(ct);
        var requested = ParseLimits(form);
        var api = apiFactory.Create(instance, context.Request);

        foreach (var hash in SplitHashes(form["hashes"].ToString()))
        {
            using var _ = await locks.AcquireAsync($"{group.Key}|{hash.ToLowerInvariant()}", ct);
            var torrent = (await api.GetTorrentsAsync([hash], ct)).FirstOrDefault(t =>
                t.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase)
            );
            var merged = torrent is null ? requested : requested.Merge(torrent.ShareLimits);
            // Pin Stop while the torrent is managed; qBt 2.15 also 400s without the action param.
            await api.SetShareLimitsAsync([hash], merged, ShareLimitAction.Stop, ct);
        }

        return OkResult;
    }

    // ---- torrents/setCategory -------------------------------------------------------------------

    public Task<IResult> HandleSetCategoryAsync(
        HttpContext context,
        ClientInstanceConfig instance
    ) => GuardAsync(instance, "setCategory", () => SetCategoryAsync(context, instance));

    private async Task<IResult> SetCategoryAsync(HttpContext context, ClientInstanceConfig instance)
    {
        var ct = context.RequestAborted;
        var form = await context.Request.ReadFormAsync(ct);
        var category = form["category"].ToString();
        RememberCategory(instance, category);

        var hashes = SplitHashes(form["hashes"].ToString());
        if (hashes.Count > 0)
        {
            var api = apiFactory.Create(instance, context.Request);
            await api.RemoveTagsAsync(hashes, [instance.Name], ct);
            if (!string.IsNullOrEmpty(category))
            {
                await api.AddTagsAsync(hashes, [category], ct);
            }
        }

        return OkResult;
    }

    // ---- helpers --------------------------------------------------------------------------------

    private async Task<IResult> GuardAsync(
        ClientInstanceConfig instance,
        string operation,
        Func<Task<IResult>> action
    )
    {
        try
        {
            return await action();
        }
        catch (QBittorrentAuthException ex)
        {
            // Propagate the auth failure so Radarr re-logs-in and retries.
            return Results.StatusCode(ex.StatusCode);
        }
        catch (QBittorrentUpstreamException ex)
        {
            logger.LogError(ex, "qBittorrent dedup operation failed", ("Operation", operation));
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
    }

    private static async Task RaiseShareLimitsAsync(
        QBittorrentApiClient api,
        IReadOnlyList<TorrentInfo> torrents,
        ShareLimits requested,
        CancellationToken ct
    )
    {
        foreach (var torrent in torrents)
        {
            var merged = requested.Merge(torrent.ShareLimits);
            if (merged != torrent.ShareLimits)
            {
                await api.SetShareLimitsAsync([torrent.Hash], merged, ShareLimitAction.Stop, ct);
            }
        }
    }

    private static async Task<List<UploadedFile>> ReadFilesAsync(
        IFormCollection form,
        CancellationToken ct
    )
    {
        var files = new List<UploadedFile>(form.Files.Count);
        foreach (var file in form.Files)
        {
            await using var stream = file.OpenReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            files.Add(
                new UploadedFile(file.Name, file.FileName, file.ContentType, buffer.ToArray())
            );
        }

        return files;
    }

    private static (List<string> Hashes, bool AnyUnparseable) DeriveHashes(
        IFormCollection form,
        IReadOnlyList<UploadedFile> files
    )
    {
        var hashes = new List<string>();
        var anyUnparseable = false;

        foreach (var file in files)
        {
            if (BencodeInfoHash.TryComputeV1(file.Bytes, out var hash))
            {
                hashes.Add(hash);
            }
            else
            {
                anyUnparseable = true;
            }
        }

        var urls = form["urls"].ToString();
        if (!string.IsNullOrWhiteSpace(urls))
        {
            foreach (
                var url in urls.Split(
                    ['\n', '\r'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                )
            )
            {
                if (
                    url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
                    && MagnetUri.TryGetInfoHash(url, out var hash)
                )
                {
                    hashes.Add(hash);
                }
                else
                {
                    anyUnparseable = true; // plain http(s) URL or malformed magnet
                }
            }
        }

        return (hashes.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), anyUnparseable);
    }

    private static HttpContent RebuildAddForm(
        IFormCollection form,
        IReadOnlyList<UploadedFile> files,
        ClientInstanceConfig instance
    )
    {
        var dedupeCategory = instance.Dedupe?.Category;
        var tags = BuildTags(form, instance);

        if (files.Count > 0)
        {
            var content = new MultipartFormDataContent();
            foreach (var file in files)
            {
                var part = new ByteArrayContent(file.Bytes);
                if (!string.IsNullOrEmpty(file.ContentType))
                {
                    part.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
                }

                content.Add(part, file.Field, file.FileName);
            }

            foreach (var (key, values) in EnumerateForwardedFields(form))
            {
                foreach (var value in values)
                {
                    content.Add(new StringContent(value ?? ""), key);
                }
            }

            if (!string.IsNullOrEmpty(dedupeCategory))
            {
                content.Add(new StringContent(dedupeCategory), "category");
            }

            content.Add(new StringContent(tags), "tags");
            return content;
        }

        var fields = new List<KeyValuePair<string, string>>();
        foreach (var (key, values) in EnumerateForwardedFields(form))
        {
            fields.AddRange(
                values.Select(value => new KeyValuePair<string, string>(key, value ?? ""))
            );
        }

        if (!string.IsNullOrEmpty(dedupeCategory))
        {
            fields.Add(new("category", dedupeCategory));
        }

        fields.Add(new("tags", tags));
        return new FormUrlEncodedContent(fields);
    }

    private static IEnumerable<
        KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>
    > EnumerateForwardedFields(IFormCollection form) =>
        form.Where(field =>
            !field.Key.Equals("category", StringComparison.OrdinalIgnoreCase)
            && !field.Key.Equals("tags", StringComparison.OrdinalIgnoreCase)
        );

    private static string BuildTags(IFormCollection form, ClientInstanceConfig instance)
    {
        var tags = form["tags"]
            .ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (!tags.Contains(instance.Name, StringComparer.OrdinalIgnoreCase))
        {
            tags.Add(instance.Name);
        }

        return string.Join(',', tags);
    }

    private static ShareLimits ParseLimits(IFormCollection form) =>
        new(
            ParseDouble(form["ratioLimit"]),
            ParseLong(form["seedingTimeLimit"]),
            ParseLong(form["inactiveSeedingTimeLimit"])
        );

    private static bool NeedsGlobalPrefs(ShareLimits limits) =>
        limits.RatioLimit == ShareLimits.Global
        || limits.SeedingTimeLimit == ShareLimits.GlobalTime;

    private static bool IsAddSuccess(System.Net.HttpStatusCode status, string body) =>
        status is >= (System.Net.HttpStatusCode)200 and < (System.Net.HttpStatusCode)300
        && !body.Trim().Equals("Fails.", StringComparison.OrdinalIgnoreCase);

    private static List<string> SplitHashes(string raw) =>
        raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private static double? ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static long? ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static JsonNode? TryParse(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private void RememberCategory(ClientInstanceConfig instance, string? category)
    {
        if (!string.IsNullOrEmpty(category))
        {
            KnownCategories(instance)[category] = 0;
        }
    }

    private ConcurrentDictionary<string, byte> KnownCategories(ClientInstanceConfig instance) =>
        _knownCategories.GetOrAdd(
            instance.Name,
            _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
        );

    private sealed record UploadedFile(
        string Field,
        string FileName,
        string ContentType,
        byte[] Bytes
    );
}
