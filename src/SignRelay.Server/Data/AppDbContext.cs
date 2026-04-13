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
        job.Property(e => e.ManifestJson).HasMaxLength(64_000);
        job.Property(e => e.LeaseAgentId).HasMaxLength(256);
        job.Property(e => e.ErrorMessage).HasMaxLength(16_000);
        job.HasIndex(e => e.Status);
        job.HasIndex(e => e.CreatedUtc);
        job.HasIndex(e => e.JobTokenHash);
    }
}
