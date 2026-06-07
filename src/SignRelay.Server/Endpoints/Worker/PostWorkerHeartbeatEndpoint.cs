using System.Security.Claims;
using FastEndpoints;
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

    public PostWorkerHeartbeatEndpoint(JobService jobs) => _jobs = jobs;

    public override void Configure()
    {
        Post($"{SignRelay.Contracts.ApiRoutes.Prefix}/worker/jobs/{{jobId}}/heartbeat");
        AuthSchemes(SignRelayAuthenticationHandler.SchemeName);
        Policies("Lease");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var jobId = Route<string>("jobId")!;
        var claimedJobId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (claimedJobId != jobId)
        {
            await Send.ForbiddenAsync(ct).ConfigureAwait(false);
            return;
        }

        await _jobs.ExtendLeaseAsync(jobId, ct).ConfigureAwait(false);
        await Send.OkAsync(ct).ConfigureAwait(false);
    }
}
