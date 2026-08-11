using Microsoft.EntityFrameworkCore;
using Proxyarr.Dedupe.Db;
using Proxyarr.Logging;

namespace Proxyarr.Dedupe;

/// <summary>
/// Persists SABnzbd dedup state (jobs and per-instance claims) in SQLite via EF Core. A context is
/// created per operation through <see cref="IDbContextFactory{TContext}"/> since the store is a
/// singleton. Callers serialize same-item mutations with the keyed async lock, so these operations
/// only contend across different items — WAL plus the busy-timeout pragma handle that.
/// </summary>
public sealed class ClaimStore(
    IDbContextFactory<ProxyarrDbContext> factory,
    ILogger<ClaimStore> logger
)
{
    /// <summary>Applies pending migrations, creating the database if needed. Call once at startup.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);
        logger.LogInformation(
            "SABnzbd dedup database ready",
            ("DataSource", db.Database.GetDbConnection().DataSource)
        );
    }

    public async Task<Job?> GetByContentKeyAsync(
        string groupKey,
        string contentKey,
        CancellationToken cancellationToken
    )
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db
            .Jobs.Include(job => job.Claims)
            .AsNoTracking()
            .FirstOrDefaultAsync(
                job => job.GroupKey == groupKey && job.ContentKey == contentKey,
                cancellationToken
            );
    }

    public async Task<Job?> GetByNzoIdAsync(
        string groupKey,
        string nzoId,
        CancellationToken cancellationToken
    )
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db
            .Jobs.Include(job => job.Claims)
            .AsNoTracking()
            .FirstOrDefaultAsync(
                job => job.GroupKey == groupKey && job.NzoId == nzoId,
                cancellationToken
            );
    }

    /// <summary>
    /// Inserts a new job with its first claim. If a concurrent insert already created the job
    /// (unique <c>(GroupKey, ContentKey)</c> race), returns the existing row instead.
    /// </summary>
    public async Task<Job> AddJobAsync(
        string groupKey,
        string contentKey,
        string nzoId,
        string instance,
        string category,
        CancellationToken cancellationToken
    )
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var job = new Job
        {
            GroupKey = groupKey,
            ContentKey = contentKey,
            NzoId = nzoId,
            CreatedAt = now,
            LastSeenAt = now,
            Claims =
            {
                new Claim
                {
                    Instance = instance,
                    Category = category,
                    CreatedAt = now,
                },
            },
        };
        db.Jobs.Add(job);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return job;
        }
        catch (DbUpdateException ex)
        {
            db.ChangeTracker.Clear();
            return await GetByContentKeyAsync(groupKey, contentKey, cancellationToken)
                ?? throw new InvalidOperationException(
                    "Job insert conflicted but the existing row could not be read back",
                    ex
                );
        }
    }

    /// <summary>Adds (or refreshes the category of) an instance's claim on an existing job.</summary>
    public async Task AddClaimAsync(
        int jobId,
        string instance,
        string category,
        CancellationToken cancellationToken
    )
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Claims.FindAsync([jobId, instance], cancellationToken);
        if (existing is null)
        {
            db.Claims.Add(
                new Claim
                {
                    JobId = jobId,
                    Instance = instance,
                    Category = category,
                    CreatedAt = DateTime.UtcNow,
                }
            );
        }
        else
        {
            existing.Category = category;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Removes an instance's claim, returning how many claims remain on the job.</summary>
    public async Task<int> RemoveClaimAsync(
        string groupKey,
        string nzoId,
        string instance,
        CancellationToken cancellationToken
    )
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var job = await db
            .Jobs.Include(entity => entity.Claims)
            .FirstOrDefaultAsync(
                entity => entity.GroupKey == groupKey && entity.NzoId == nzoId,
                cancellationToken
            );
        if (job is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return 0;
        }

        var claim = job.Claims.FirstOrDefault(entity =>
            entity.Instance.Equals(instance, StringComparison.OrdinalIgnoreCase)
        );
        if (claim is not null)
        {
            job.Claims.Remove(claim);
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return job.Claims.Count;
    }

    public async Task DeleteJobAsync(int jobId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Jobs.Where(job => job.Id == jobId).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task UpdateNzoIdAsync(
        string groupKey,
        string oldNzoId,
        string newNzoId,
        CancellationToken cancellationToken
    )
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db
            .Jobs.Where(job => job.GroupKey == groupKey && job.NzoId == oldNzoId)
            .ExecuteUpdateAsync(
                setter => setter.SetProperty(job => job.NzoId, newNzoId),
                cancellationToken
            );
    }

    /// <summary>Maps each requested nzo_id the instance still claims to the category it sent at add time.</summary>
    public async Task<Dictionary<string, string>> GetClaimedCategoriesByNzoIdAsync(
        string groupKey,
        string instance,
        IReadOnlyCollection<string> nzoIds,
        CancellationToken cancellationToken
    )
    {
        if (nzoIds.Count == 0)
        {
            return [];
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await db
            .Claims.Where(claim =>
                claim.Instance == instance
                && claim.Job.GroupKey == groupKey
                && nzoIds.Contains(claim.Job.NzoId)
            )
            .Select(claim => new { claim.Job.NzoId, claim.Category })
            .ToListAsync(cancellationToken);

        return rows.GroupBy(row => row.NzoId)
            .ToDictionary(group => group.Key, group => group.First().Category);
    }

    /// <summary>Distinct non-empty category names the instance claims — injected into get_config.</summary>
    public async Task<List<string>> GetClaimedCategoryNamesAsync(
        string groupKey,
        string instance,
        CancellationToken cancellationToken
    )
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db
            .Claims.Where(claim =>
                claim.Instance == instance && claim.Job.GroupKey == groupKey && claim.Category != ""
            )
            .Select(claim => claim.Category)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Touches <c>LastSeenAt</c> for the nzo_ids seen in a listing and drops any job in the group not
    /// seen anywhere for <paramref name="graceWindow"/> — self-heals rows orphaned outside the proxy.
    /// </summary>
    public async Task ReconcileAsync(
        string groupKey,
        IReadOnlyCollection<string> seenNzoIds,
        TimeSpan graceWindow,
        CancellationToken cancellationToken
    )
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;

        if (seenNzoIds.Count > 0)
        {
            await db
                .Jobs.Where(job => job.GroupKey == groupKey && seenNzoIds.Contains(job.NzoId))
                .ExecuteUpdateAsync(
                    setter => setter.SetProperty(job => job.LastSeenAt, now),
                    cancellationToken
                );
        }

        var cutoff = now - graceWindow;
        await db
            .Jobs.Where(job => job.GroupKey == groupKey && job.LastSeenAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
