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
        var opt = scope.ServiceProvider.GetRequiredService<IOptions<SignRelayOptions>>().Value;

        var now = DateTimeOffset.UtcNow;
        var expired = await db.Jobs
            .Where(j => j.ExpiresUtc <= now && j.Status != JobStatus.Succeeded && j.Status != JobStatus.Failed && j.Status != JobStatus.TimedOut)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var j in expired)
        {
            j.Status = JobStatus.TimedOut;
            j.CompletedUtc = now;
            j.ErrorMessage = "Job expired before completion.";
            hub.Publish(j.Id, new JobEventPayload { Type = "done", Status = JobStatus.TimedOut, Error = j.ErrorMessage });
            _log.LogWarning("Job {JobId} timed out.", j.Id);
        }

        if (expired.Count > 0)
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
