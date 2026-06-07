using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SignRelay.Contracts;
using SignRelay.Server.Data;
using SignRelay.Server.Options;

namespace SignRelay.Server.Services;

public sealed class JobSweeper : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<JobSweeper> _log;

    public JobSweeper(IServiceProvider services, ILogger<JobSweeper> log)
    {
        _services = services;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Job sweep failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SweepOnceAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hub = scope.ServiceProvider.GetRequiredService<JobEventHub>();
        var jobSvc = scope.ServiceProvider.GetRequiredService<JobService>();
        var opt = scope.ServiceProvider.GetRequiredService<IOptions<SignRelayOptions>>().Value;

        var now = DateTimeOffset.UtcNow;
        var terminalFloor = (int)JobStatus.Succeeded;

        // Jobs that have hit their overall TTL without reaching a terminal state
        var expired = await db.Jobs
            .FromSqlInterpolated(
                $"""
                 SELECT * FROM Jobs
                 WHERE ExpiresUtc <= {now} AND Status < {terminalFloor}
                 """)
            .AsTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var j in expired)
        {
            j.Status = JobStatus.TimedOut;
            j.CompletedUtc = now;
            j.ErrorMessage = "Job expired before completion.";
            j.LeaseTokenHash = null;
            hub.Publish(j.Id, new JobEventPayload { Type = "done", Status = JobStatus.TimedOut, Error = j.ErrorMessage });
            _log.LogWarning("Job {JobId} timed out.", j.Id);
        }

        // Jobs whose lease has expired but overall TTL has not — requeue or permanently fail
        var staleLeases = await db.Jobs
            .FromSqlInterpolated(
                $"""
                 SELECT * FROM Jobs
                 WHERE Status IN ({(int)JobStatus.Leased}, {(int)JobStatus.Signing})
                   AND LeaseExpiresUtc <= {now}
                   AND ExpiresUtc > {now}
                 """)
            .AsTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var j in staleLeases)
        {
            if (j.LeaseAttempts >= opt.MaxLeaseAttempts)
            {
                j.Status = JobStatus.Failed;
                j.CompletedUtc = now;
                j.ErrorMessage = $"Job exceeded maximum lease attempts ({opt.MaxLeaseAttempts}).";
                j.LeaseTokenHash = null;
                hub.Publish(j.Id, new JobEventPayload { Type = "done", Status = JobStatus.Failed, Error = j.ErrorMessage });
                _log.LogWarning("Job {JobId} permanently failed: too many lease attempts.", j.Id);
            }
            else
            {
                // Requeue: reset to Pending so another agent can pick it up
                j.Status = JobStatus.Pending;
                j.LeaseAgentId = null;
                j.LeasedUtc = null;
                j.LeaseTokenHash = null;
                j.LeaseExpiresUtc = null;
                _log.LogInformation("Job {JobId} requeued after stale lease (attempt {Attempt}/{Max}).",
                    j.Id, j.LeaseAttempts, opt.MaxLeaseAttempts);
            }
        }

        if (expired.Count > 0 || staleLeases.Count > 0)
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Disk cleanup: delete artifact directories for jobs that reached a terminal state
        // longer ago than the configured grace period
        var cleanupCutoff = now - opt.ArtifactCleanupDelay;
        var toClean = await db.Jobs
            .FromSqlInterpolated(
                $"""
                 SELECT * FROM Jobs
                 WHERE Status >= {terminalFloor}
                   AND CompletedUtc <= {cleanupCutoff}
                 """)
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var j in toClean)
        {
            var dir = jobSvc.GetJobArtifactDirectory(j.Id);
            if (!Directory.Exists(dir))
                continue;
            try
            {
                Directory.Delete(dir, recursive: true);
                _log.LogInformation("Deleted artifacts for job {JobId}.", j.Id);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not delete artifact directory for job {JobId}.", j.Id);
            }
        }
    }
}
