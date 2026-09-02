using System.Security.Claims;
using FastEndpoints;
using SignRelay.Contracts;
using SignRelay.Server.Api;
using SignRelay.Server.Auth;
using SignRelay.Server.Services;

namespace SignRelay.Server.Api.Worker;

/// <summary>
/// Extends the lease expiry for the calling agent's job. The agent should call this periodically
/// (e.g. between signing individual files) to prevent the sweeper from requeuing a slow job.
/// </summary>
public sealed class PostWorkerHeartbeatEndpoint : EndpointWithoutRequest
{
    private readonly JobService _jobs;
    private readonly ILogger<PostWorkerHeartbeatEndpoint> _log;

    public PostWorkerHeartbeatEndpoint(JobService jobs, ILogger<PostWorkerHeartbeatEndpoint> log)
    {
        _jobs = jobs;
        _log = log;
    }

    public override void Configure()
    {
        Post($"{ApiRoutes.Prefix}/worker/jobs/{{jobId}}/heartbeat");
        AuthSchemes(SignRelayAuthenticationHandler.SchemeName);
        Policies("Lease");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!JobRoute.TryBind(Route<string>("jobId"), out var jobId))
        {
            AddError("Job id is invalid.");
            ServerHttpError.Log(_log, HttpContext, 400, "Job id is invalid.");
            await Send.ErrorsAsync(400, ct).ConfigureAwait(false);
            return;
        }

        var claimedJobId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (claimedJobId != jobId)
        {
            ServerHttpError.Log(_log, HttpContext, 403, "Lease job id mismatch.");
            await Send.ForbiddenAsync(ct).ConfigureAwait(false);
            return;
        }

        await _jobs.ExtendLeaseAsync(jobId, ct).ConfigureAwait(false);
        await Send.OkAsync(ct).ConfigureAwait(false);
    }
}
