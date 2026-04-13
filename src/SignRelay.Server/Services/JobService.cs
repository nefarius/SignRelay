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

    public async Task<(JobEntity Job, string PlainJobToken)> CreateJobAsync(JobManifestDto manifest, IReadOnlyList<(string RelativePath, Stream Content, long Length)> files, CancellationToken ct)
    {
        if (manifest.Files.Count != files.Count)
            throw new InvalidOperationException("Manifest file count does not match uploaded files.");

        long total = 0;
        foreach (var f in files)
        {
            total += f.Length;
            if (total > _opt.MaxTotalJobBytes)
                throw new InvalidOperationException($"Job exceeds MaxTotalJobBytes ({_opt.MaxTotalJobBytes}).");
        }

        for (var i = 0; i < manifest.Files.Count; i++)
        {
            PathSafety.NormalizeRelativePath(manifest.Files[i].RelativePath);
            if (!string.Equals(manifest.Files[i].RelativePath, files[i].RelativePath, StringComparison.Ordinal))
                throw new InvalidOperationException("File order and manifest paths must match.");
        }

        var id = Guid.NewGuid().ToString("N");
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
            ManifestJson = JsonSerializer.Serialize(manifest, Json),
            TotalUnsignedBytes = total
        };

        _db.Jobs.Add(entity);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var root = Path.Combine(JobsRoot, "jobs", id, "unsigned");
        Directory.CreateDirectory(root);

        for (var i = 0; i < files.Count; i++)
        {
            var rel = PathSafety.NormalizeRelativePath(files[i].RelativePath);
            var dest = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await using (var fs = File.Create(dest))
            {
                await files[i].Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            if (files[i].Content.CanSeek)
                files[i].Content.Position = 0;
        }

        _hub.Publish(id, new JobEventPayload { Type = "status", Status = JobStatus.Pending, Error = null });
        return (entity, jobToken);
    }

    public async Task<LeaseResult?> TryLeaseAsync(string? agentId, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var next = await _db.Jobs
            .Where(j => j.Status == JobStatus.Pending && j.ExpiresUtc > DateTimeOffset.UtcNow)
            .OrderBy(j => j.CreatedUtc)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (next is null)
        {
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return null;
        }

        next.Status = JobStatus.Leased;
        next.LeasedUtc = DateTimeOffset.UtcNow;
        next.LeaseAgentId = agentId;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        var manifest = JsonSerializer.Deserialize<JobManifestDto>(next.ManifestJson, Json);
        if (manifest is null)
        {
            await FailJobAsync(next.Id, "Invalid manifest in database.", ct).ConfigureAwait(false);
            return null;
        }

        var paths = manifest.Files.Select(f => ApiRoutes.WorkerUnsigned(next.Id, f.RelativePath)).ToList();
        _hub.Publish(next.Id, new JobEventPayload { Type = "status", Status = JobStatus.Leased, Error = null });

        return new LeaseResult(next.Id, manifest, paths);
    }

    public async Task<Stream?> OpenUnsignedAsync(string jobId, string relativePath, CancellationToken ct)
    {
        PathSafety.NormalizeRelativePath(relativePath);
        var job = await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct).ConfigureAwait(false);
        if (job is null)
            return null;
        if (job.Status is not (JobStatus.Leased or JobStatus.Signing))
            return null;

        var rel = PathSafety.NormalizeRelativePath(relativePath);
        var root = Path.GetFullPath(Path.Combine(JobsRoot, "jobs", jobId, "unsigned"));
        var path = Path.GetFullPath(Path.Combine(root, rel));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
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

        job.Status = JobStatus.Signing;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var manifest = JsonSerializer.Deserialize<JobManifestDto>(job.ManifestJson, Json)
                       ?? throw new InvalidOperationException("Invalid manifest.");

        if (orderedSignedFiles.Count != manifest.Files.Count)
            throw new InvalidOperationException("Signed file count does not match manifest.");

        var signedRoot = Path.Combine(JobsRoot, "jobs", jobId, "signed");
        Directory.CreateDirectory(signedRoot);

        for (var i = 0; i < manifest.Files.Count; i++)
        {
            var entry = manifest.Files[i];
            var form = orderedSignedFiles[i];
            if (form.Length == 0)
                throw new InvalidOperationException($"Missing signed file for '{entry.RelativePath}'.");

            var rel = PathSafety.NormalizeRelativePath(entry.RelativePath);
            var dest = Path.Combine(signedRoot, rel);
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
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _hub.Publish(jobId, new JobEventPayload { Type = "done", Status = JobStatus.Succeeded, Error = null });
        _log.LogInformation("Job {JobId} signed successfully.", jobId);
    }

    public async Task FailJobAsync(string jobId, string error, CancellationToken ct)
    {
        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct).ConfigureAwait(false);
        if (job is null)
            return;

        job.Status = JobStatus.Failed;
        job.CompletedUtc = DateTimeOffset.UtcNow;
        job.ErrorMessage = error;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _hub.Publish(jobId, new JobEventPayload { Type = "done", Status = JobStatus.Failed, Error = error });
        _log.LogWarning("Job {JobId} failed: {Error}", jobId, error);
    }

    public async Task<JobEntity?> GetJobAsync(string jobId, CancellationToken ct) =>
        await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct).ConfigureAwait(false);

    public async Task<Stream?> OpenSignedAsync(string jobId, string relativePath, CancellationToken ct)
    {
        PathSafety.NormalizeRelativePath(relativePath);
        var job = await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct).ConfigureAwait(false);
        if (job is null || job.Status != JobStatus.Succeeded)
            return null;

        var rel = PathSafety.NormalizeRelativePath(relativePath);
        var root = Path.GetFullPath(Path.Combine(JobsRoot, "jobs", jobId, "signed"));
        var path = Path.GetFullPath(Path.Combine(root, rel));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return null;

        return File.Exists(path) ? File.OpenRead(path) : null;
    }

    public JobEventPayload ToPayload(JobEntity j) =>
        new()
        {
            Type = j.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.TimedOut ? "done" : "status",
            Status = j.Status,
            Error = j.ErrorMessage
        };
}

public sealed record LeaseResult(string JobId, JobManifestDto Manifest, IReadOnlyList<string> UnsignedDownloadPaths);
