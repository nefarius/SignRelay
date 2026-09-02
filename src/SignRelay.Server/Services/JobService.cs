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

            // Promote files BEFORE inserting the DB row so that a failed Directory.Move
            // never leaves an orphaned Pending record pointing at a missing directory.
            Directory.CreateDirectory(Path.GetDirectoryName(finalRoot)!);
            Directory.Move(stagingRoot, finalRoot);

            // Optionally clean up the now-empty parent staging dir
            var stagingParent = Path.GetDirectoryName(stagingRoot)!;
            if (Directory.Exists(stagingParent))
                TrySilentDelete(stagingParent, recursive: false);

            _db.Jobs.Add(entity);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            _hub.Publish(id, new JobEventPayload { Type = "status", Status = JobStatus.Pending, Error = null });
            return (entity, jobToken);
        }
        catch
        {
            // On any failure: clean up both the staging dir (pre-move) and the final dir
            // (post-move) so no orphaned files remain regardless of where we failed.
            TrySilentDelete(Path.GetDirectoryName(stagingRoot)!, recursive: true);
            TrySilentDelete(Path.GetDirectoryName(finalRoot)!, recursive: true);
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

        var paths = Enumerable.Range(0, manifest.Files.Count)
            .Select(i => ApiRoutes.WorkerUnsignedByIndex(next.Id, i))
            .ToList();
        _hub.Publish(next.Id, new JobEventPayload { Type = "status", Status = JobStatus.Leased, Error = null });

        return new LeaseResult(next.Id, manifest, paths, plainLeaseToken, leaseExpiry);
    }

    public IReadOnlyList<string> SignedDownloadPaths(string jobId, int fileCount)
    {
        EnsureValidJobId(jobId);
        return Enumerable.Range(0, fileCount).Select(i => ApiRoutes.JobSignedFileByIndex(jobId, i)).ToList();
    }

    public async Task<Stream?> OpenUnsignedAsync(string jobId, string relativePath, CancellationToken ct)
    {
        EnsureValidJobId(jobId);
        var job = await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct).ConfigureAwait(false);
        if (job is null)
            return null;
        if (job.Status is not (JobStatus.Leased or JobStatus.Signing))
            return null;

        return OpenJobFile(jobId, "unsigned", relativePath);
    }

    public async Task<(Stream Stream, string FileName)?> OpenUnsignedByIndexAsync(string jobId, int index, CancellationToken ct)
    {
        EnsureValidJobId(jobId);
        var job = await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct).ConfigureAwait(false);
        if (job is null || job.Status is not (JobStatus.Leased or JobStatus.Signing))
            return null;

        if (!TryManifestFile(job, index, out var entry))
            return null;

        var stream = OpenJobFile(jobId, "unsigned", entry.RelativePath);
        if (stream is null)
            return null;
        return (stream, Path.GetFileName(entry.RelativePath));
    }

    public async Task SaveSignedFilesAsync(string jobId, IReadOnlyList<IFormFile> orderedSignedFiles, CancellationToken ct)
    {
        EnsureValidJobId(jobId);
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

        var signedRoot = ResolveJobSubdir(jobId, "signed");
        Directory.CreateDirectory(signedRoot);

        var tmpFiles = new List<string>();
        try
        {
            for (var i = 0; i < manifest.Files.Count; i++)
            {
                var entry = manifest.Files[i];
                var form = orderedSignedFiles[i];
                if (form.Length == 0)
                    throw new InvalidOperationException($"Missing signed file for '{entry.RelativePath}'.");

                var rel = PathSafety.NormalizeRelativePath(entry.RelativePath);
                var dest = Path.GetFullPath(Path.Combine(signedRoot, rel));
                if (!PathSafety.IsUnderRoot(dest, signedRoot) || !IsUnderJobsRoot(signedRoot))
                    throw new InvalidOperationException($"Path escape detected for '{rel}'.");

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

                // Write to a temp file first; atomically promote on success so CompleteJobAsync's
                // File.Exists check can trust that a present file is complete.
                var tmpPath = dest + "." + Guid.NewGuid().ToString("N") + ".tmp";
                tmpFiles.Add(tmpPath);
                await using (var fs = File.Create(tmpPath))
                {
                    await form.CopyToAsync(fs, ct).ConfigureAwait(false);
                }
                File.Move(tmpPath, dest, overwrite: true);
                tmpFiles.RemoveAt(tmpFiles.Count - 1); // committed — no longer needs cleanup
            }
        }
        catch
        {
            foreach (var tmp in tmpFiles)
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }

        _hub.Publish(jobId, new JobEventPayload { Type = "status", Status = JobStatus.Signing, Error = null });
    }

    public async Task CompleteJobAsync(string jobId, CancellationToken ct)
    {
        EnsureValidJobId(jobId);
        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct).ConfigureAwait(false)
                  ?? throw new InvalidOperationException("Job not found.");

        if (job.Status is not (JobStatus.Leased or JobStatus.Signing))
            throw new InvalidOperationException("Job is not in a completable state.");

        var manifest = JsonSerializer.Deserialize<JobManifestDto>(job.ManifestJson, Json)
                       ?? throw new InvalidOperationException("Invalid manifest.");

        var signedRoot = ResolveJobSubdir(jobId, "signed");
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
        EnsureValidJobId(jobId);
        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct).ConfigureAwait(false);
        if (job is null)
            return;

        // Only transition from non-terminal states
        if (job.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.TimedOut)
            return;

        _log.LogWarning("Job {JobId} failed: {Error}", jobId, error);
        var persisted = HttpFailureDetails.Persist(error);

        job.Status = JobStatus.Failed;
        job.CompletedUtc = DateTimeOffset.UtcNow;
        job.ErrorMessage = persisted;
        job.LeaseTokenHash = null;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _hub.Publish(jobId, new JobEventPayload { Type = "done", Status = JobStatus.Failed, Error = persisted });
    }

    public async Task ExtendLeaseAsync(string jobId, CancellationToken ct)
    {
        EnsureValidJobId(jobId);
        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct).ConfigureAwait(false);
        if (job is null || job.Status is not (JobStatus.Leased or JobStatus.Signing))
            return;

        job.LeaseExpiresUtc = DateTimeOffset.UtcNow.Add(_opt.LeaseDuration);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<JobEntity?> GetJobAsync(string jobId, CancellationToken ct)
    {
        EnsureValidJobId(jobId);
        return await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct).ConfigureAwait(false);
    }

    public async Task<Stream?> OpenSignedAsync(string jobId, string relativePath, CancellationToken ct)
    {
        EnsureValidJobId(jobId);
        var job = await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct).ConfigureAwait(false);
        if (job is null || job.Status != JobStatus.Succeeded)
            return null;

        return OpenJobFile(jobId, "signed", relativePath);
    }

    public async Task<(Stream Stream, string FileName)?> OpenSignedByIndexAsync(string jobId, int index, CancellationToken ct)
    {
        EnsureValidJobId(jobId);
        var job = await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct).ConfigureAwait(false);
        if (job is null || job.Status != JobStatus.Succeeded)
            return null;

        if (!TryManifestFile(job, index, out var entry))
            return null;

        var stream = OpenJobFile(jobId, "signed", entry.RelativePath);
        if (stream is null)
            return null;
        return (stream, Path.GetFileName(entry.RelativePath));
    }

    public string GetJobArtifactDirectory(string jobId) => ResolveJobDir(jobId);

    public JobEventPayload ToPayload(JobEntity j) =>
        new()
        {
            Type = j.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.TimedOut ? "done" : "status",
            Status = j.Status,
            Error = j.ErrorMessage
        };

    internal static void EnsureValidJobId(string jobId)
    {
        if (!JobIdFormat.IsValid(jobId))
            throw new InvalidOperationException("Invalid job id.");
    }

    internal string ResolveJobDir(string jobId)
    {
        EnsureValidJobId(jobId);
        var jobsRoot = Path.GetFullPath(Path.Combine(JobsRoot, "jobs"));
        var dir = Path.GetFullPath(Path.Combine(jobsRoot, jobId));
        if (!PathSafety.IsUnderRoot(dir, jobsRoot))
            throw new InvalidOperationException("Job artifact path escaped the storage root.");
        return dir;
    }

    internal string ResolveJobSubdir(string jobId, string kind)
    {
        var jobDir = ResolveJobDir(jobId);
        var sub = Path.GetFullPath(Path.Combine(jobDir, kind));
        if (!PathSafety.IsUnderRoot(sub, jobDir))
            throw new InvalidOperationException("Job artifact path escaped the storage root.");
        return sub;
    }

    internal bool IsUnderJobsRoot(string fullPath)
    {
        var jobsRoot = Path.GetFullPath(Path.Combine(JobsRoot, "jobs"));
        return PathSafety.IsUnderRoot(fullPath, jobsRoot)
               || string.Equals(Path.GetFullPath(fullPath), jobsRoot, OperatingSystem.IsWindows()
                   ? StringComparison.OrdinalIgnoreCase
                   : StringComparison.Ordinal);
    }

    private Stream? OpenJobFile(string jobId, string kind, string relativePath)
    {
        var rel = PathSafety.NormalizeRelativePath(relativePath);
        var root = ResolveJobSubdir(jobId, kind);
        var path = Path.GetFullPath(Path.Combine(root, rel));
        if (!PathSafety.IsUnderRoot(path, root) || !IsUnderJobsRoot(path))
            return null;
        return File.Exists(path) ? File.OpenRead(path) : null;
    }

    private bool TryManifestFile(JobEntity job, int index, out JobFileEntry entry)
    {
        entry = null!;
        if (index < 0)
            return false;
        var manifest = JsonSerializer.Deserialize<JobManifestDto>(job.ManifestJson, Json);
        if (manifest?.Files is null || index >= manifest.Files.Count)
            return false;
        entry = manifest.Files[index];
        return true;
    }

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
