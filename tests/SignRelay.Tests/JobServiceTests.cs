using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignRelay.Contracts;
using SignRelay.Server.Data;
using SignRelay.Server.Options;
using SignRelay.Server.Services;

namespace SignRelay.Tests;

/// <summary>
/// Unit tests for <see cref="JobService"/> covering state-transition guards,
/// lease model, and path safety using an in-memory SQLite database.
/// </summary>
public sealed class JobServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly JobEventHub _hub;
    private readonly JobService _sut;
    private readonly string _storagePath;

    public JobServiceTests()
    {
        _storagePath = Path.Combine(Path.GetTempPath(), "signrelay-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_storagePath);

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_storagePath, "test.db")}")
            .Options;
        _db = new AppDbContext(dbOptions);
        _db.Database.EnsureCreated();

        _hub = new JobEventHub();

        var opts = Options.Create(new SignRelayOptions
        {
            StoragePath = _storagePath,
            JobTimeToLive = TimeSpan.FromHours(1),
            LeaseDuration = TimeSpan.FromMinutes(30),
            MaxLeaseAttempts = 3,
        });

        _sut = new JobService(_db, opts, _hub, NullLogger<JobService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_storagePath, recursive: true); } catch { }
    }

    // ---- Helper ----

    private async Task<(JobEntity Job, string Token)> CreateTestJobAsync()
    {
        var manifest = new JobManifestDto
        {
            Files = [new JobFileEntry { RelativePath = "file.exe" }]
        };

        await using var ms = new MemoryStream(new byte[] { 0x4D, 0x5A }); // minimal PE header
        var files = new List<(string RelativePath, Stream Content, long Length)>
        {
            ("file.exe", ms, ms.Length)
        };

        return await _sut.CreateJobAsync(manifest, files, CancellationToken.None);
    }

    // ---- State-transition guard: FailJobAsync must not overwrite terminal states ----

    [Fact]
    public async Task FailJobAsync_OnSucceededJob_DoesNotTransition()
    {
        var (job, _) = await CreateTestJobAsync();

        // Manually transition to Succeeded
        var entity = await _db.Jobs.FindAsync(job.Id);
        entity!.Status = JobStatus.Succeeded;
        entity.CompletedUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        await _sut.FailJobAsync(job.Id, "late failure", CancellationToken.None);

        var refreshed = await _db.Jobs.AsNoTracking().FirstAsync(j => j.Id == job.Id);
        Assert.Equal(JobStatus.Succeeded, refreshed.Status);
    }

    [Fact]
    public async Task FailJobAsync_OnPendingJob_Transitions()
    {
        var (job, _) = await CreateTestJobAsync();

        await _sut.FailJobAsync(job.Id, "deliberate fail", CancellationToken.None);

        var refreshed = await _db.Jobs.AsNoTracking().FirstAsync(j => j.Id == job.Id);
        Assert.Equal(JobStatus.Failed, refreshed.Status);
        Assert.Equal("deliberate fail", refreshed.ErrorMessage);
    }

    [Fact]
    public async Task FailJobAsync_OnAlreadyFailedJob_IsIdempotent()
    {
        var (job, _) = await CreateTestJobAsync();
        await _sut.FailJobAsync(job.Id, "first", CancellationToken.None);
        await _sut.FailJobAsync(job.Id, "second", CancellationToken.None);

        var refreshed = await _db.Jobs.AsNoTracking().FirstAsync(j => j.Id == job.Id);
        // The error should remain "first" — second call was a no-op
        Assert.Equal("first", refreshed.ErrorMessage);
    }

    // ---- Lease model ----

    [Fact]
    public async Task TryLeaseAsync_PendingJob_TransitionsToLeased()
    {
        await CreateTestJobAsync();

        var result = await _sut.TryLeaseAsync("agent-1", CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result.PlainLeaseToken));
        var leased = await _db.Jobs.AsNoTracking().FirstAsync();
        Assert.Equal(JobStatus.Leased, leased.Status);
        Assert.NotNull(leased.LeaseTokenHash);
        Assert.NotNull(leased.LeaseExpiresUtc);
    }

    [Fact]
    public async Task TryLeaseAsync_NoPendingJobs_ReturnsNull()
    {
        var result = await _sut.TryLeaseAsync("agent-1", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task TryLeaseAsync_SameJobLeasedTwice_SecondCallReturnsNull()
    {
        await CreateTestJobAsync();

        var first = await _sut.TryLeaseAsync("agent-1", CancellationToken.None);
        var second = await _sut.TryLeaseAsync("agent-2", CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public async Task TryLeaseAsync_ClearsLeaseTokenOnComplete()
    {
        var (job, _) = await CreateTestJobAsync();
        var lease = await _sut.TryLeaseAsync("agent-1", CancellationToken.None);
        Assert.NotNull(lease);

        // Produce a dummy signed file so CompleteJobAsync is satisfied
        var signedRoot = Path.Combine(_storagePath, "jobs", job.Id, "signed");
        Directory.CreateDirectory(signedRoot);
        await File.WriteAllBytesAsync(Path.Combine(signedRoot, "file.exe"), [0x4D, 0x5A]);

        await _sut.CompleteJobAsync(job.Id, CancellationToken.None);

        var refreshed = await _db.Jobs.AsNoTracking().FirstAsync(j => j.Id == job.Id);
        Assert.Equal(JobStatus.Succeeded, refreshed.Status);
        Assert.Null(refreshed.LeaseTokenHash);
    }

    // ---- CreateJobAsync: path safety ----

    [Fact]
    public async Task CreateJobAsync_DuplicateRelativePaths_Throws()
    {
        var manifest = new JobManifestDto
        {
            Files =
            [
                new JobFileEntry { RelativePath = "file.exe" },
                new JobFileEntry { RelativePath = "file.exe" }
            ]
        };

        var files = new List<(string RelativePath, Stream Content, long Length)>
        {
            ("file.exe", new MemoryStream([0]), 1),
            ("file.exe", new MemoryStream([0]), 1)
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.CreateJobAsync(manifest, files, CancellationToken.None));
    }

    [Fact]
    public async Task CreateJobAsync_TraversalPath_Throws()
    {
        var manifest = new JobManifestDto
        {
            Files = [new JobFileEntry { RelativePath = "../escape.exe" }]
        };

        var files = new List<(string RelativePath, Stream Content, long Length)>
        {
            ("../escape.exe", new MemoryStream([0]), 1)
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.CreateJobAsync(manifest, files, CancellationToken.None));
    }

    [Fact]
    public async Task TryLeaseAsync_EmitsIndexedUnsignedPaths()
    {
        var (job, _) = await CreateTestJobAsync();
        var lease = await _sut.TryLeaseAsync("agent-1", CancellationToken.None);
        Assert.NotNull(lease);
        Assert.Equal(ApiRoutes.WorkerUnsignedByIndex(job.Id, 0), lease.UnsignedDownloadPaths[0]);
        Assert.DoesNotContain("%2F", lease.UnsignedDownloadPaths[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenUnsignedByIndex_OutOfRange_ReturnsNull()
    {
        var (job, _) = await CreateTestJobAsync();
        await _sut.TryLeaseAsync("agent-1", CancellationToken.None);
        Assert.Null(await _sut.OpenUnsignedByIndexAsync(job.Id, 5, CancellationToken.None));
        Assert.Null(await _sut.OpenUnsignedByIndexAsync(job.Id, -1, CancellationToken.None));
    }

    [Fact]
    public async Task OpenSignedAsync_InvalidJobId_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.OpenSignedAsync("../jobs/other", "file.exe", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.OpenSignedAsync("not-hex", "file.exe", CancellationToken.None));
    }

    [Fact]
    public async Task FailJobAsync_PersistsTruncationMarker()
    {
        var (job, _) = await CreateTestJobAsync();
        var huge = new string('x', HttpFailureDetails.PersistMaxChars + 80);
        await _sut.FailJobAsync(job.Id, huge, CancellationToken.None);
        var refreshed = await _db.Jobs.AsNoTracking().FirstAsync(j => j.Id == job.Id);
        Assert.Equal(HttpFailureDetails.PersistMaxChars, refreshed.ErrorMessage!.Length);
        Assert.EndsWith(HttpFailureDetails.TruncationMarker, refreshed.ErrorMessage);
        Assert.Equal(JobStatus.Failed, refreshed.Status);
    }

    [Fact]
    public void ResolveJobDir_Rejects_InvalidId()
    {
        Assert.Throws<InvalidOperationException>(() => _sut.ResolveJobDir("../../../etc"));
    }

    [Fact]
    public async Task CreateJobAsync_EmptyRelativePath_Throws()
    {
        var manifest = new JobManifestDto
        {
            Files = [new JobFileEntry { RelativePath = "" }]
        };

        var files = new List<(string RelativePath, Stream Content, long Length)>
        {
            ("", new MemoryStream([0]), 1)
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.CreateJobAsync(manifest, files, CancellationToken.None));
    }
}
