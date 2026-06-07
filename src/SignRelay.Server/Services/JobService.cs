using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SignRelay.Contracts;
using SignRelay.Server.Data;
using SignRelay.Server.Options;

namespace SignRelay.Server.Services;

public sealed class JobService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    private readonly AppDbContext _db;
    private readonly SignRelayOptions _opt;
    private readonly JobEventHub _hub;
    private readonly ILogger<JobService> _log;

    public JobService(AppDbContext db, IOptions<SignRelayOptions> opt, JobEventHub hub, ILogger<JobService> log)
    {
        _db = db;
        _opt = opt.Value;
        _hub = hub;
        _log = log;
    }

    public string JobsRoot => Path.GetFullPath(_opt.StoragePath);

    public async Task<(JobEntity Job, string PlainJobToken)> CreateJobAsync(
        JobManifestDto manifest,
        IReadOnlyList<(string RelativePath, Stream Content, long Length)> files,
        CancellationToken ct)
    {
        if (manifest.Files is not { Count: > 0 })
            throw new InvalidOperationException("Manifest must contain at least one file entry.");

        if (manifest.Files.Count != files.Count)
            throw new InvalidOperationException("Manifest file count does not match uploaded files.");

        // Validate and normalise all relative paths before touching the DB or disk
        var normalizedPaths = new List<string>(manifest.Files.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < manifest.Files.Count; i++)
        {
            var rel = PathSafety.NormalizeRelativePath(manifest.Files[i].RelativePath);
            if (!seen.Add(rel))
                throw new InvalidOperationException($"Duplicate relative path '{rel}' in manifest.");
            if (!string.Equals(manifest.Files[i].RelativePath, files[i].RelativePath, StringComparison.Ordinal))
                throw new InvalidOperationException("File order and manifest paths must match.");
            normalizedPaths.Add(rel);
        }

        long total = 0;
        foreach (var f in files)
        {
            total += f.Length;
            if (total > _opt.MaxTotalJobBytes)
                throw new InvalidOperationException($"Job exceeds MaxTotalJobBytes ({_opt.MaxTotalJobBytes}).");
        }

        var id = Guid.NewGuid().ToString("N");
        var stagingRoot = Path.Combine(JobsRoot, "staging", id, "unsigned");
        var finalRoot = Path.Combine(JobsRoot, "jobs", id, "unsigned");

        // Write files to a staging directory BEFORE inserting the DB row.
        // On any failure, the staging dir is cleaned up and no DB record is created.
        Directory.CreateDirectory(stagingRoot);
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var dest = Path.GetFullPath(Path.Combine(stagingRoot, normalizedPaths[i]));
                if (!PathSafety.IsUnderRoot(dest, stagingRoot))
                    throw new InvalidOperationException($"Path escape detected for '{normalizedPaths[i]}'.");
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                await using (var fs = File.Create(dest))
                {
                    await files[i].Content.CopyToAsync(fs, ct).ConfigureAwait(false);
                }

                if (files[i].Content.CanSeek)
                    files[i].Content.Position = 0;
            }

            // Persist the normalised manifest (canonical backslash/forward-slash form)
            var normalizedManifest = new JobManifestDto
            {
                Files = manifest.Files.Select((f, i) => new JobFileEntry
                {
                    RelativePath = normalizedPaths[i],
                    SignToolExtraArgs = f.SignToolExtraArgs
                }).ToList()
            };

            var jobToken = Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray());
            var tokenHash = CryptoUtil.Sha256Hex(jobToken);
            var now = DateTimeOffset.UtcNow;
            var entity = new JobEntity
            {
                Id = id,
                Status = JobStatus.Pending,
                CreatedUtc = now,
                ExpiresUtc = now.Add(_opt.JobTimeToLive),
                JobTokenHash = tokenHash,
                ManifestJson = JsonSerializer.Serialize(normalizedManifest, Json),
                TotalUnsignedBytes = total
            };

            _db.Jobs.Add(entity);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            // Atomically move staged files into the live job directory
            Directory.CreateDirectory(Path.GetDirectoryName(finalRoot)!);
            Directory.Move(stagingRoot, finalRoot);

            // Optionally clean up the now-empty parent staging dir
            var stagingParent = Path.GetDirectoryName(stagingRoot)!;
            if (Directory.Exists(stagingParent))
                TrySilentDelete(stagingParent, recursive: false);

            _hub.Publish(id, new JobEventPayload { Type = "status", Status = JobStatus.Pending, Error = null });
            return (entity, jobToken);
        }
        catch
        {
            TrySilentDelete(Path.GetDirectoryName(stagingRoot)!, recursive: true);
            throw;
        }
    }

    public async Task<LeaseResult?> TryLeaseAsync(string? agentId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var leaseExpiry = now.Add(_opt.LeaseDuration);
        var pending = (int)JobStatus.Pending;

        // Mint a lease token before the DB call so we never commit without it
        var plainLeaseToken = Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray());
        var leaseTokenHash = CryptoUtil.Sha256Hex(plainLeaseToken);

        // Atomic UPDATE: only one concurrent caller can flip a Pending row to Leased.
        // SQLite's UPDATE…WHERE is serialised by WAL; the RETURNING clause gives us the
        // chosen row's Id without a separate SELECT.
        var updated = await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE Jobs
             SET Status         = {(int)JobStatus.Leased},
                 LeasedUtc      = {now},
                 LeaseAgentId   = {agentId},
                 LeaseTokenHash = {leaseTokenHash},
                 LeaseExpiresUtc = {leaseExpiry},
                 LeaseAttempts  = LeaseAttempts + 1
             WHERE Id = (
                 SELECT Id FROM Jobs
                 WHERE Status = {pending}
                   AND ExpiresUtc > {now}
                   AND LeaseAttempts < {_opt.MaxLeaseAttempts}
                 ORDER BY CreatedUtc
                 LIMIT 1
             )
             """,
            ct).ConfigureAwait(false);

        if (updated == 0)
            return null;

        // The raw SQL update bypassed EF tracking — clear stale tracked entities so that
        // subsequent FirstOrDefaultAsync calls in this request always see fresh data.
        _db.ChangeTracker.Clear();

        // Fetch the row we just leased (the one with our leaseTokenHash)
        var next = await _db.Jobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.LeaseTokenHash == leaseTokenHash, ct)
            .ConfigureAwait(false);

        if (next is null)
        {
            // Should not happen; defensive fallback
            _log.LogWarning("Lease was recorded but the row could not be found (hash: {Hash}).", leaseTokenHash);
            return null;
        }

        var manifest = JsonSerializer.Deserialize<JobManifestDto>(next.ManifestJson, Json);
        if (manifest is null)
        {
            await FailJobAsync(next.Id, "Invalid manifest in database.", ct).ConfigureAwait(false);
            return null;
        }

        var paths = manifest.Files.Select(f => ApiRoutes.WorkerUnsigned(next.Id, f.RelativePath)).ToList();
        _hub.Publish(next.Id, new JobEventPayload { Type = "status", Status = JobStatus.Leased, Error = null });

        return new LeaseResult(next.Id, manifest, paths, plainLeaseToken, leaseExpiry);
    }

    public async Task<Stream?> OpenUnsignedAsync(string jobId, string relativePath, CancellationToken ct)
    {
        var job = await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct).ConfigureAwait(false);
        if (job is null)
            return null;
        if (job.Status is not (JobStatus.Leased or JobStatus.Signing))
            return null;

        var rel = PathSafety.NormalizeRelativePath(relativePath);
        var root = Path.GetFullPath(Path.Combine(JobsRoot, "jobs", jobId, "unsigned"));
        var path = Path.GetFullPath(Path.Combine(root, rel));
        if (!PathSafety.IsUnderRoot(path, root))
            return null;
        if (!File.Exists(path))
            return null;

        return File.OpenRead(path);
    }

    public async Task SaveSignedFilesAsync(string jobId, IReadOnlyList<IFormFile> orderedSignedFiles, CancellationToken ct)
    {
        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct).ConfigureAwait(false)
                  ?? throw new InvalidOperationException("Job not found.");

        if (job.Status is not (JobStatus.Leased or JobStatus.Signing))
            throw new InvalidOperationException("Job is not in a signable state.");

        var manifest = JsonSerializer.Deserialize<JobManifestDto>(job.ManifestJson, Json)
                       ?? throw new InvalidOperationException("Invalid manifest.");

        if (orderedSignedFiles.Count != manifest.Files.Count)
            throw new InvalidOperationException("Signed file count does not match manifest.");

        // Validate total upload size against original job size (rough bound)
        long uploadTotal = orderedSignedFiles.Sum(f => f.Length);
        if (uploadTotal == 0 || uploadTotal > _opt.MaxTotalJobBytes * 2)
            throw new InvalidOperationException("Signed upload size is out of expected range.");

        job.Status = JobStatus.Signing;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var signedRoot = Path.Combine(JobsRoot, "jobs", jobId, "signed");
        Directory.CreateDirectory(signedRoot);

        for (var i = 0; i < manifest.Files.Count; i++)
        {
            var entry = manifest.Files[i];
            var form = orderedSignedFiles[i];
            if (form.Length == 0)
                throw new InvalidOperationException($"Missing signed file for '{entry.RelativePath}'.");

            var rel = PathSafety.NormalizeRelativePath(entry.RelativePath);
            var dest = Path.GetFullPath(Path.Combine(signedRoot, rel));
            if (!PathSafety.IsUnderRoot(dest, signedRoot))
                throw new InvalidOperationException($"Path escape detected for '{rel}'.");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await using (var fs = File.Create(dest))
            {
                await form.CopyToAsync(fs, ct).ConfigureAwait(false);
            }
        }

        _hub.Publish(jobId, new JobEventPayload { Type = "status", Status = JobStatus.Signing, Error = null });
    }

    public async Task CompleteJobAsync(string jobId, CancellationToken ct)
    {
        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct).ConfigureAwait(false)
                  ?? throw new InvalidOperationException("Job not found.");

        if (job.Status is not (JobStatus.Leased or JobStatus.Signing))
            throw new InvalidOperationException("Job is not in a completable state.");

        var manifest = JsonSerializer.Deserialize<JobManifestDto>(job.ManifestJson, Json)
                       ?? throw new InvalidOperationException("Invalid manifest.");

        var signedRoot = Path.Combine(JobsRoot, "jobs", jobId, "signed");
        foreach (var entry in manifest.Files)
        {
            var rel = PathSafety.NormalizeRelativePath(entry.RelativePath);
            var path = Path.Combine(signedRoot, rel);
            if (!File.Exists(path))
                throw new InvalidOperationException($"Signed artifact missing for '{entry.RelativePath}'.");
        }

        job.Status = JobStatus.Succeeded;
        job.CompletedUtc = DateTimeOffset.UtcNow;
        job.ErrorMessage = null;
        // Clear lease token so it can no longer be used
        job.LeaseTokenHash = null;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _hub.Publish(jobId, new JobEventPayload { Type = "done", Status = JobStatus.Succeeded, Error = null });
        _log.LogInformation("Job {JobId} signed successfully.", jobId);
    }

    public async Task FailJobAsync(string jobId, string error, CancellationToken ct)
    {
        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct).ConfigureAwait(false);
        if (job is null)
            return;

        // Only transition from non-terminal states
        if (job.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.TimedOut)
            return;

        var truncated = error.Length > 16_000 ? error[..16_000] : error;

        job.Status = JobStatus.Failed;
        job.CompletedUtc = DateTimeOffset.UtcNow;
        job.ErrorMessage = truncated;
        job.LeaseTokenHash = null;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _hub.Publish(jobId, new JobEventPayload { Type = "done", Status = JobStatus.Failed, Error = truncated });
        _log.LogWarning("Job {JobId} failed: {Error}", jobId, truncated);
    }

    public async Task ExtendLeaseAsync(string jobId, CancellationToken ct)
    {
        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct).ConfigureAwait(false);
        if (job is null || job.Status is not (JobStatus.Leased or JobStatus.Signing))
            return;

        job.LeaseExpiresUtc = DateTimeOffset.UtcNow.Add(_opt.LeaseDuration);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<JobEntity?> GetJobAsync(string jobId, CancellationToken ct) =>
        await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct).ConfigureAwait(false);

    public async Task<Stream?> OpenSignedAsync(string jobId, string relativePath, CancellationToken ct)
    {
        var job = await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct).ConfigureAwait(false);
        if (job is null || job.Status != JobStatus.Succeeded)
            return null;

        var rel = PathSafety.NormalizeRelativePath(relativePath);
        var root = Path.GetFullPath(Path.Combine(JobsRoot, "jobs", jobId, "signed"));
        var path = Path.GetFullPath(Path.Combine(root, rel));
        if (!PathSafety.IsUnderRoot(path, root))
            return null;

        return File.Exists(path) ? File.OpenRead(path) : null;
    }

    public string GetJobArtifactDirectory(string jobId) =>
        Path.GetFullPath(Path.Combine(JobsRoot, "jobs", jobId));

    public JobEventPayload ToPayload(JobEntity j) =>
        new()
        {
            Type = j.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.TimedOut ? "done" : "status",
            Status = j.Status,
            Error = j.ErrorMessage
        };

    private static void TrySilentDelete(string path, bool recursive)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive);
        }
        catch
        {
            // best-effort
        }
    }
}

public sealed record LeaseResult(
    string JobId,
    JobManifestDto Manifest,
    IReadOnlyList<string> UnsignedDownloadPaths,
    string PlainLeaseToken,
    DateTimeOffset LeaseExpiresUtc);
