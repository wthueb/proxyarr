using Proxyarr.Clients.QBittorrent;

namespace Proxyarr.Tests;

public class ShareLimitsTests
{
    [Theory]
    [InlineData(1.0, 2.0, 2.0)] // finite values merge by max
    [InlineData(-1, 5.0, -1)] // unlimited beats any finite
    [InlineData(5.0, -1, -1)]
    [InlineData(-2, 3.0, 3.0)] // global loses to explicit
    [InlineData(-2, -2, -2)] // both global stays global
    [InlineData(-1, -2, -1)] // unlimited beats global
    public void Merges_ratio_by_the_max_rule(double a, double b, double expected)
    {
        Assert.Equal(expected, ShareLimits.MergeRatio(a, b));
    }

    [Fact]
    public void Merge_keeps_current_when_a_field_is_absent()
    {
        var requested = new ShareLimits(RatioLimit: 2.0, SeedingTimeLimit: null, null);
        var current = new ShareLimits(RatioLimit: 1.0, SeedingTimeLimit: 120, null);

        var merged = requested.Merge(current);

        Assert.Equal(2.0, merged.RatioLimit);
        Assert.Equal(120, merged.SeedingTimeLimit); // untouched
    }

    [Fact]
    public void Merge_max_merges_present_fields()
    {
        var requested = new ShareLimits(2.0, 30, -1);
        var current = new ShareLimits(1.0, 90, 45);

        var merged = requested.Merge(current);

        Assert.Equal(2.0, merged.RatioLimit);
        Assert.Equal(90, merged.SeedingTimeLimit);
        Assert.Equal(-1, merged.InactiveSeedingTimeLimit); // unlimited wins
    }

    [Fact]
    public void IsSurpassed_true_when_ratio_reached()
    {
        var limits = new ShareLimits(RatioLimit: 1.5, SeedingTimeLimit: -1, -1);

        Assert.True(limits.IsSurpassed(ratio: 1.6, seedingTimeSeconds: 0, GlobalShareLimits.None));
        Assert.False(limits.IsSurpassed(ratio: 1.0, seedingTimeSeconds: 0, GlobalShareLimits.None));
    }

    [Fact]
    public void IsSurpassed_true_when_seeding_minutes_reached()
    {
        // 30-minute limit; seeding_time is in seconds.
        var limits = new ShareLimits(RatioLimit: -1, SeedingTimeLimit: 30, -1);

        Assert.True(limits.IsSurpassed(0, seedingTimeSeconds: 31 * 60, GlobalShareLimits.None));
        Assert.False(limits.IsSurpassed(0, seedingTimeSeconds: 29 * 60, GlobalShareLimits.None));
    }

    [Fact]
    public void IsSurpassed_never_for_unlimited_limits()
    {
        var limits = new ShareLimits(RatioLimit: -1, SeedingTimeLimit: -1, -1);

        Assert.False(limits.IsSurpassed(999.0, seedingTimeSeconds: 999999, GlobalShareLimits.None));
    }

    [Fact]
    public void IsSurpassed_resolves_global_ratio_from_preferences()
    {
        var limits = new ShareLimits(RatioLimit: -2, SeedingTimeLimit: -2, -2);
        var global = new GlobalShareLimits(
            MaxRatioEnabled: true,
            MaxRatio: 2.0,
            MaxSeedingTimeEnabled: false,
            MaxSeedingTimeMinutes: -1
        );

        Assert.True(limits.IsSurpassed(2.5, 0, global));
        Assert.False(limits.IsSurpassed(1.0, 0, global));
    }

    [Fact]
    public void IsSurpassed_global_disabled_means_never()
    {
        var limits = new ShareLimits(RatioLimit: -2, SeedingTimeLimit: -2, -2);

        Assert.False(limits.IsSurpassed(999.0, 999999, GlobalShareLimits.None));
    }
}
