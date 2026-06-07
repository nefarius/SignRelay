using Microsoft.EntityFrameworkCore;

namespace SignRelay.Server.Data;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<JobEntity> Jobs => Set<JobEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var job = modelBuilder.Entity<JobEntity>();
        job.HasKey(e => e.Id);
        job.Property(e => e.Id).HasMaxLength(64);
        job.Property(e => e.JobTokenHash).HasMaxLength(128);
        job.Property(e => e.LeaseTokenHash).HasMaxLength(128);
        job.Property(e => e.ManifestJson).HasMaxLength(64_000);
        job.Property(e => e.LeaseAgentId).HasMaxLength(256);
        job.Property(e => e.ErrorMessage).HasMaxLength(16_000);

        // Unique job token — only one row per hash
        job.HasIndex(e => e.JobTokenHash).IsUnique();

        // Lease token lookup (nullable, so not unique at DB level; uniqueness enforced in code)
        job.HasIndex(e => e.LeaseTokenHash);

        // Sweeper queries: status + expiry
        job.HasIndex(e => new { e.Status, e.ExpiresUtc });

        job.HasIndex(e => e.CreatedUtc);
    }
}
