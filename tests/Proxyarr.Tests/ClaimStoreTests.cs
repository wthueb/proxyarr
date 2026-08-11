using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Proxyarr.Dedupe;
using Proxyarr.Dedupe.Db;

namespace Proxyarr.Tests;

/// <summary>
/// Exercises the claim store against a real temporary SQLite file (not the EF in-memory provider),
/// so the checked-in migration, unique indexes, cascade delete, and concurrent access are all
/// covered.
/// </summary>
public sealed class ClaimStoreTests : IAsyncLifetime
{
    private const string Group = "sabnzbd|url:http://sab:8080";

    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"proxyarr-claims-{Guid.NewGuid():N}.db"
    );
    private ClaimStore _store = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = new ClaimStore(new TestDbFactory(_path), NullLogger<ClaimStore>.Instance);
        await _store.InitializeAsync(Ct);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(_path + suffix);
            }
            catch (IOException)
            {
                // best effort
            }
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Adds_a_job_and_reads_it_back_by_content_key()
    {
        await _store.AddJobAsync(Group, "content-1", "nzo-1", "radarr1", "movies", Ct);

        var job = await _store.GetByContentKeyAsync(Group, "content-1", Ct);

        Assert.NotNull(job);
        Assert.Equal("nzo-1", job!.NzoId);
        var claim = Assert.Single(job.Claims);
        Assert.Equal("radarr1", claim.Instance);
        Assert.Equal("movies", claim.Category);
    }

    [Fact]
    public async Task A_conflicting_insert_returns_the_existing_job()
    {
        var first = await _store.AddJobAsync(Group, "dup", "nzo-a", "radarr1", "movies", Ct);
        var second = await _store.AddJobAsync(Group, "dup", "nzo-b", "radarr2", "tv", Ct);

        Assert.Equal(first.Id, second.Id); // the unique index kept it to one row
        Assert.Equal("nzo-a", second.NzoId); // original id preserved
    }

    [Fact]
    public async Task Claim_counting_drives_the_last_owner_check()
    {
        var job = await _store.AddJobAsync(Group, "shared", "nzo-s", "radarr1", "movies", Ct);
        await _store.AddClaimAsync(job.Id, "radarr2", "tv", Ct);

        Assert.Equal(1, await _store.RemoveClaimAsync(Group, "nzo-s", "radarr1", Ct));
        Assert.Equal(0, await _store.RemoveClaimAsync(Group, "nzo-s", "radarr2", Ct));
    }

    [Fact]
    public async Task Deleting_a_job_cascades_to_its_claims()
    {
        var job = await _store.AddJobAsync(Group, "gone", "nzo-g", "radarr1", "movies", Ct);
        await _store.AddClaimAsync(job.Id, "radarr2", "tv", Ct);

        await _store.DeleteJobAsync(job.Id, Ct);

        await using var db = new TestDbFactory(_path).CreateDbContext();
        Assert.Empty(db.Jobs);
        Assert.Empty(db.Claims); // cascade removed the claims
    }

    [Fact]
    public async Task Maps_claimed_categories_by_nzo_id_per_instance()
    {
        await _store.AddJobAsync(Group, "c-a", "nzo-a", "radarr1", "movies", Ct);
        var jobB = await _store.AddJobAsync(Group, "c-b", "nzo-b", "radarr1", "docs", Ct);
        await _store.AddClaimAsync(jobB.Id, "radarr2", "shows", Ct);

        var forRadarr1 = await _store.GetClaimedCategoriesByNzoIdAsync(
            Group,
            "radarr1",
            ["nzo-a", "nzo-b"],
            Ct
        );

        Assert.Equal("movies", forRadarr1["nzo-a"]);
        Assert.Equal("docs", forRadarr1["nzo-b"]);

        var names = await _store.GetClaimedCategoryNamesAsync(Group, "radarr2", Ct);
        Assert.Equal(["shows"], names);
    }

    [Fact]
    public async Task Updates_the_nzo_id_after_a_retry()
    {
        await _store.AddJobAsync(Group, "retry", "old-nzo", "radarr1", "movies", Ct);

        await _store.UpdateNzoIdAsync(Group, "old-nzo", "new-nzo", Ct);

        Assert.Null(await _store.GetByNzoIdAsync(Group, "old-nzo", Ct));
        Assert.NotNull(await _store.GetByNzoIdAsync(Group, "new-nzo", Ct));
    }

    [Fact]
    public async Task Reconcile_prunes_jobs_unseen_past_the_grace_window()
    {
        await _store.AddJobAsync(Group, "stale", "nzo-stale", "radarr1", "movies", Ct);
        await _store.AddJobAsync(Group, "fresh", "nzo-fresh", "radarr1", "movies", Ct);

        // Zero grace: everything not in the "seen" set is immediately older than the cutoff.
        await _store.ReconcileAsync(Group, ["nzo-fresh"], TimeSpan.Zero, Ct);

        Assert.Null(await _store.GetByNzoIdAsync(Group, "nzo-stale", Ct));
        Assert.NotNull(await _store.GetByNzoIdAsync(Group, "nzo-fresh", Ct));
    }

    [Fact]
    public async Task Concurrent_claim_mutations_are_consistent()
    {
        var job = await _store.AddJobAsync(Group, "conc", "nzo-c", "seed", "movies", Ct);

        // Add 20 distinct claims concurrently; WAL + busy_timeout must serialize the writes.
        await Task.WhenAll(
            Enumerable
                .Range(0, 20)
                .Select(i => _store.AddClaimAsync(job.Id, $"inst-{i}", "cat", Ct))
        );

        await using var db = new TestDbFactory(_path).CreateDbContext();
        Assert.Equal(21, db.Claims.Count(claim => claim.JobId == job.Id)); // seed + 20
    }

    private sealed class TestDbFactory(string path) : IDbContextFactory<ProxyarrDbContext>
    {
        public ProxyarrDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<ProxyarrDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path }.ToString())
                .AddInterceptors(new SqlitePragmaInterceptor())
                .Options;
            return new ProxyarrDbContext(options);
        }
    }
}
