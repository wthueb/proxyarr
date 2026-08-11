namespace Proxyarr.Clients.QBittorrent;

/// <summary>
/// qBittorrent per-torrent share-limit "action when a limit is reached", passed to
/// <c>torrents/setShareLimits</c> as the <c>shareLimitAction</c> parameter and echoed back in
/// <c>torrents/info</c>'s <c>share_limit_action</c> field.
///
/// Verified against qBittorrent 5.2.3 / Web API 2.15.1: the parameter takes the enum's *string name*
/// — an integer is silently coerced to "Default", so these must stay strings.
/// </summary>
public static class ShareLimitAction
{
    /// <summary>Use the global "when ratio limit reached" behavior.</summary>
    public const string Default = "Default";

    /// <summary>Stop (pause) the torrent — never deletes anything. Pinned while managed tags exist.</summary>
    public const string Stop = "Stop";

    /// <summary>Remove the torrent from qBittorrent but keep its files.</summary>
    public const string Remove = "Remove";

    /// <summary>Remove the torrent and delete its files.</summary>
    public const string RemoveWithContent = "RemoveWithContent";
}

/// <summary>
/// Global share limits read from <c>app/preferences</c>, used to resolve a torrent's <c>-2</c>
/// ("use global") ratio/seeding-time limits at delete time.
/// </summary>
public readonly record struct GlobalShareLimits(
    bool MaxRatioEnabled,
    double MaxRatio,
    bool MaxSeedingTimeEnabled,
    long MaxSeedingTimeMinutes
)
{
    /// <summary>No global limits configured — every <c>-2</c> resolves to "never".</summary>
    public static readonly GlobalShareLimits None = new(false, -1, false, -1);
}

/// <summary>
/// A qBittorrent share-limit triple (ratio / seeding time / inactive seeding time). A null field
/// means "not specified by this source" (keep the other side's value when merging). The sentinel
/// values follow qBittorrent: <c>-1</c> = unlimited/never, <c>-2</c> = use the global limit.
/// Seeding-time limits are in minutes; the torrent's elapsed <c>seeding_time</c> is in seconds.
/// </summary>
public readonly record struct ShareLimits(
    double? RatioLimit,
    long? SeedingTimeLimit,
    long? InactiveSeedingTimeLimit
)
{
    public const double Unlimited = -1;
    public const double Global = -2;
    public const long UnlimitedTime = -1;
    public const long GlobalTime = -2;

    /// <summary>
    /// Max-merge these (typically the limits an *arr just requested) with a torrent's
    /// <paramref name="current"/> limits: <c>-1</c> unlimited beats all, finite values take the max,
    /// <c>-2</c> global loses to any explicit value. A null field here keeps the current value.
    /// </summary>
    public ShareLimits Merge(ShareLimits current) =>
        new(
            RatioLimit is { } ratio
                ? MergeRatio(ratio, current.RatioLimit ?? Global)
                : current.RatioLimit,
            SeedingTimeLimit is { } seeding
                ? MergeTime(seeding, current.SeedingTimeLimit ?? GlobalTime)
                : current.SeedingTimeLimit,
            InactiveSeedingTimeLimit is { } inactive
                ? MergeTime(inactive, current.InactiveSeedingTimeLimit ?? GlobalTime)
                : current.InactiveSeedingTimeLimit
        );

    /// <summary>
    /// True when the torrent has already passed one of its effective ratio/seeding-time limits, so a
    /// real delete-with-files is safe now instead of handing cleanup to qBittorrent. <c>-2</c> limits
    /// resolve through <paramref name="global"/>; <c>-1</c> and disabled globals mean "never".
    /// </summary>
    public bool IsSurpassed(double ratio, long seedingTimeSeconds, GlobalShareLimits global)
    {
        if (ResolveRatio(global) is { } ratioLimit && ratio >= ratioLimit)
        {
            return true;
        }

        if (
            ResolveSeedingMinutes(global) is { } seedingMinutes
            && seedingTimeSeconds >= seedingMinutes * 60
        )
        {
            return true;
        }

        return false;
    }

    private double? ResolveRatio(GlobalShareLimits global)
    {
        var limit = RatioLimit ?? Global;
        if (limit == Unlimited)
        {
            return null;
        }

        return limit == Global ? (global.MaxRatioEnabled ? global.MaxRatio : null) : limit;
    }

    private long? ResolveSeedingMinutes(GlobalShareLimits global)
    {
        var limit = SeedingTimeLimit ?? GlobalTime;
        if (limit == UnlimitedTime)
        {
            return null;
        }

        return limit == GlobalTime
            ? (global.MaxSeedingTimeEnabled ? global.MaxSeedingTimeMinutes : null)
            : limit;
    }

    public static double MergeRatio(double a, double b) => MergeValue(a, b, Unlimited, Global);

    public static long MergeTime(long a, long b) =>
        (long)MergeValue(a, b, UnlimitedTime, GlobalTime);

    private static double MergeValue(double a, double b, double unlimited, double global)
    {
        if (a == unlimited || b == unlimited)
        {
            return unlimited;
        }

        var aExplicit = a != global;
        var bExplicit = b != global;
        return (aExplicit, bExplicit) switch
        {
            (true, true) => Math.Max(a, b),
            (true, false) => a,
            (false, true) => b,
            _ => global,
        };
    }
}
