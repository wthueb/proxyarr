using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Proxyarr.Dedupe.Db;

/// <summary>
/// One deduplicated SABnzbd download. A job is the single upstream NZB add shared by every instance
/// that grabbed the same release (identified within its group by <see cref="ContentKey"/>), tracked
/// by its upstream <see cref="NzoId"/>. The job's files live until its last <see cref="Claim"/> is
/// removed.
/// </summary>
public sealed class Job
{
    public int Id { get; set; }

    /// <summary>The dedup group this job belongs to (see <see cref="DedupeGroups"/>).</summary>
    public string GroupKey { get; set; } = "";

    /// <summary>Stable content key derived from the NZB's segment message-IDs.</summary>
    public string ContentKey { get; set; } = "";

    /// <summary>The upstream SABnzbd <c>nzo_id</c> the job currently lives under.</summary>
    public string NzoId { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    /// <summary>Last time the job was seen in a queue/history listing; drives stale pruning.</summary>
    public DateTime LastSeenAt { get; set; }

    public ICollection<Claim> Claims { get; set; } = new List<Claim>();
}

/// <summary>One instance's ownership of a <see cref="Job"/>, plus the category it originally sent.</summary>
public sealed class Claim
{
    public int JobId { get; set; }

    /// <summary>The proxyarr instance (URL prefix) that holds this claim.</summary>
    public string Instance { get; set; } = "";

    /// <summary>The category the instance sent at add time, echoed back in listing responses.</summary>
    public string Category { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    public Job Job { get; set; } = null!;
}

/// <summary>
/// EF Core context backing SABnzbd dedup. Used through <c>IDbContextFactory</c> (a context per
/// operation) because the services that consume it are singletons.
/// </summary>
public sealed class ProxyarrDbContext(DbContextOptions<ProxyarrDbContext> options)
    : DbContext(options)
{
    public DbSet<Job> Jobs => Set<Job>();

    public DbSet<Claim> Claims => Set<Claim>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var job = modelBuilder.Entity<Job>();
        job.HasKey(entity => entity.Id);
        job.HasIndex(entity => new { entity.GroupKey, entity.ContentKey }).IsUnique();
        job.HasIndex(entity => new { entity.GroupKey, entity.NzoId }).IsUnique();
        job.HasMany(entity => entity.Claims)
            .WithOne(claim => claim.Job)
            .HasForeignKey(claim => claim.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Claim>().HasKey(claim => new { claim.JobId, claim.Instance });
    }
}

/// <summary>
/// Design-time factory so <c>dotnet ef migrations</c> can build the model without booting the app.
/// Never used at runtime (the app configures the real path via <c>AddDbContextFactory</c>).
/// </summary>
public sealed class ProxyarrDbContextFactory : IDesignTimeDbContextFactory<ProxyarrDbContext>
{
    public ProxyarrDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ProxyarrDbContext>()
            .UseSqlite("Data Source=proxyarr-design.db")
            .Options;
        return new ProxyarrDbContext(options);
    }
}
