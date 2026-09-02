using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignRelay.Contracts;
using SignRelay.Server.Data;
using SignRelay.Server.Options;
using SignRelay.Server.Services;

namespace SignRelay.Tests;

/// <summary>
/// SQLite-backed tests for <see cref="JobSweeper"/> covering retention purge,
/// artifact cleanup, job expiry, and stale-lease handling.
/// </summary>
public sealed class JobSweeperTests : IDisposable
{
    private readonly string _storagePath;
    private readonly ServiceProvider _sp;
    private readonly JobEventHub _hub;
    private readonly JobSweeper _sut;
    private readonly SignRelayOptions _opts;

    public JobSweeperTests()
    {
        _storagePath = Path.Combine(Path.GetTempPath(), "signrelay-sweeper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_storagePath);

        _opts = new SignRelayOptions
        {
            StoragePath = _storagePath,
            JobTimeToLive = TimeSpan.FromHours(1),
            LeaseDuration = TimeSpan.FromMinutes(30),
            MaxLeaseAttempts = 3,
            ArtifactCleanupDelay = TimeSpan.FromHours(1),
            JobRecordRetention = TimeSpan.FromDays(7),
        };

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o =>
            o.UseSqlite($"Data Source={Path.Combine(_storagePath, "test.db")}"));
        services.AddSingleton(_hub = new JobEventHub());
        services.AddScoped<JobService>();
        services.AddSingleton(Options.Create(_opts));
        services.AddLogging();

        _sp = services.BuildServiceProvider();

        using (var scope = _sp.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        }

        _sut = new JobSweeper(_sp, NullLogger<JobSweeper>.Instance);
    }

    public void Dispose()
    {
        _sp.Dispose();
        try { Directory.Delete(_storagePath, recursive: true); } catch { /* best effort */ }
    }

    private async Task SeedAsync(params JobEntity[] jobs)
    {
        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Jobs.AddRange(jobs);
        await db.SaveChangesAsync();
    }

    private async Task<JobEntity?> FindAsync(string id)
    {
        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id);
    }

    private string ArtifactDir(string jobId) =>
        Path.GetFullPath(Path.Combine(_storagePath, "jobs", jobId));

    private void CreateArtifactDir(string jobId)
    {
        var dir = ArtifactDir(jobId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "marker.txt"), "x");
    }

    private static JobEntity MakeJob(
        string id,
        JobStatus status,
        DateTimeOffset? completedUtc = null,
        DateTimeOffset? expiresUtc = null,
        DateTimeOffset? leaseExpiresUtc = null,
        int leaseAttempts = 0,
        string? leaseTokenHash = "lease-hash",
        string? leaseAgentId = "agent-1")
    {
        var now = DateTimeOffset.UtcNow;
        return new JobEntity
        {
            Id = id,
            Status = status,
            CreatedUtc = now.AddHours(-2),
            ExpiresUtc = expiresUtc ?? now.AddHours(1),
            JobTokenHash = "job-token-" + id,
            ManifestJson = """{"files":[]}""",
            TotalUnsignedBytes = 0,
            CompletedUtc = completedUtc,
            LeaseExpiresUtc = leaseExpiresUtc,
            LeaseAttempts = leaseAttempts,
            LeaseTokenHash = leaseTokenHash,
            LeaseAgentId = leaseAgentId,
            LeasedUtc = leaseExpiresUtc.HasValue ? now.AddMinutes(-40) : null,
        };
    }

    // ---- Empty / no-op ----

    [Fact]
    public async Task SweepOnceAsync_EmptyDatabase_DoesNotThrow()
    {
        await _sut.SweepOnceAsync(CancellationToken.None);
        Assert.Null(await FindAsync("anything"));
    }

    // ---- Retention purge ----

    [Fact]
    public async Task SweepOnceAsync_PurgesTerminalJobPastRecordRetention()
    {
        var id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1";
        var completed = DateTimeOffset.UtcNow - TimeSpan.FromDays(8);
        await SeedAsync(MakeJob(id, JobStatus.Succeeded, completedUtc: completed));
        // No artifact directory — already cleared.

        await _sut.SweepOnceAsync(CancellationToken.None);

        Assert.Null(await FindAsync(id));
    }

    [Fact]
    public async Task SweepOnceAsync_KeepsRowInsideRecordRetention_DeletesArtifacts()
    {
        var id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa2";
        // Past ArtifactCleanupDelay (1h) but inside JobRecordRetention (7d)
        var completed = DateTimeOffset.UtcNow - TimeSpan.FromHours(2);
        await SeedAsync(MakeJob(id, JobStatus.Succeeded, completedUtc: completed));
        CreateArtifactDir(id);

        await _sut.SweepOnceAsync(CancellationToken.None);

        Assert.NotNull(await FindAsync(id));
        Assert.False(Directory.Exists(ArtifactDir(id)));
    }

    [Fact]
    public async Task SweepOnceAsync_DoesNotPurgeNonTerminalJob()
    {
        var id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa3";
        // Non-terminal: never eligible for cleanup/purge regardless of timestamps
        await SeedAsync(MakeJob(
            id,
            JobStatus.Pending,
            expiresUtc: DateTimeOffset.UtcNow.AddHours(1),
            leaseTokenHash: null,
            leaseAgentId: null));

        await _sut.SweepOnceAsync(CancellationToken.None);

        Assert.NotNull(await FindAsync(id));
    }

    // ---- Artifact cleanup timing ----

    [Fact]
    public async Task SweepOnceAsync_LeavesArtifactsBeforeCleanupDelay()
    {
        var id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa4";
        var completed = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10);
        await SeedAsync(MakeJob(id, JobStatus.Succeeded, completedUtc: completed));
        CreateArtifactDir(id);

        await _sut.SweepOnceAsync(CancellationToken.None);

        Assert.True(Directory.Exists(ArtifactDir(id)));
        Assert.NotNull(await FindAsync(id));
    }

    [Fact]
    public async Task SweepOnceAsync_DeletesArtifactsPastCleanupDelay()
    {
        var id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa5";
        var completed = DateTimeOffset.UtcNow - TimeSpan.FromHours(2);
        await SeedAsync(MakeJob(id, JobStatus.Failed, completedUtc: completed));
        CreateArtifactDir(id);

        await _sut.SweepOnceAsync(CancellationToken.None);

        Assert.False(Directory.Exists(ArtifactDir(id)));
        Assert.NotNull(await FindAsync(id));
    }

    // ---- Expiry ----

    [Fact]
    public async Task SweepOnceAsync_ExpiresNonTerminalJobPastTtl()
    {
        var id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa6";
        await SeedAsync(MakeJob(
            id,
            JobStatus.Pending,
            expiresUtc: DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5),
            leaseTokenHash: "still-set",
            leaseAgentId: null));

        var sub = _hub.Subscribe(id);
        try
        {
            await _sut.SweepOnceAsync(CancellationToken.None);

            var job = await FindAsync(id);
            Assert.NotNull(job);
            Assert.Equal(JobStatus.TimedOut, job.Status);
            Assert.NotNull(job.CompletedUtc);
            Assert.Equal("Job expired before completion.", job.ErrorMessage);
            Assert.Null(job.LeaseTokenHash);

            Assert.True(sub.Reader.TryRead(out var ev));
            Assert.Equal("done", ev.Type);
            Assert.Equal(JobStatus.TimedOut, ev.Status);
            Assert.Equal("Job expired before completion.", ev.Error);
        }
        finally
        {
            sub.Dispose();
        }
    }

    // ---- Stale leases ----

    [Fact]
    public async Task SweepOnceAsync_RequeuesStaleLeaseUnderMaxAttempts()
    {
        var id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa7";
        await SeedAsync(MakeJob(
            id,
            JobStatus.Leased,
            expiresUtc: DateTimeOffset.UtcNow.AddHours(1),
            leaseExpiresUtc: DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1),
            leaseAttempts: 1,
            leaseTokenHash: "lease-hash",
            leaseAgentId: "agent-1"));

        await _sut.SweepOnceAsync(CancellationToken.None);

        var job = await FindAsync(id);
        Assert.NotNull(job);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Null(job.LeaseAgentId);
        Assert.Null(job.LeasedUtc);
        Assert.Null(job.LeaseTokenHash);
        Assert.Null(job.LeaseExpiresUtc);
        Assert.Equal(1, job.LeaseAttempts);
    }

    [Fact]
    public async Task SweepOnceAsync_FailsStaleLeaseAtMaxAttempts()
    {
        var id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa8";
        await SeedAsync(MakeJob(
            id,
            JobStatus.Signing,
            expiresUtc: DateTimeOffset.UtcNow.AddHours(1),
            leaseExpiresUtc: DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1),
            leaseAttempts: 3,
            leaseTokenHash: "lease-hash",
            leaseAgentId: "agent-1"));

        var sub = _hub.Subscribe(id);
        try
        {
            await _sut.SweepOnceAsync(CancellationToken.None);

            var job = await FindAsync(id);
            Assert.NotNull(job);
            Assert.Equal(JobStatus.Failed, job.Status);
            Assert.NotNull(job.CompletedUtc);
            Assert.Equal("Job exceeded maximum lease attempts (3).", job.ErrorMessage);
            Assert.Null(job.LeaseTokenHash);

            Assert.True(sub.Reader.TryRead(out var ev));
            Assert.Equal("done", ev.Type);
            Assert.Equal(JobStatus.Failed, ev.Status);
            Assert.Equal("Job exceeded maximum lease attempts (3).", ev.Error);
        }
        finally
        {
            sub.Dispose();
        }
    }
}
