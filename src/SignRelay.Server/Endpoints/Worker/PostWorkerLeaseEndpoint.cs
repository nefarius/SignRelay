using FastEndpoints;
using SignRelay.Contracts;
using SignRelay.Server.Auth;
using SignRelay.Server.Services;

namespace SignRelay.Server.Api.Worker;

public sealed class PostWorkerLeaseEndpoint : Endpoint<WorkerLeaseRequest, LeaseResponse>
{
    private readonly JobService _jobs;

    public PostWorkerLeaseEndpoint(JobService jobs) => _jobs = jobs;

    public override void Configure()
    {
        Post(SignRelay.Contracts.ApiRoutes.WorkerLease);
        AuthSchemes(SignRelayAuthenticationHandler.SchemeName);
        Policies("Agent");
    }

    public override async Task HandleAsync(WorkerLeaseRequest req, CancellationToken ct)
    {
        var lease = await _jobs.TryLeaseAsync(req.AgentId, ct).ConfigureAwait(false);
        if (lease is null)
        {
            await Send.NoContentAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(
                new LeaseResponse
                {
                    JobId = lease.JobId,
                    Manifest = lease.Manifest,
                    UnsignedDownloadPaths = lease.UnsignedDownloadPaths
                },
                ct)
            .ConfigureAwait(false);
    }
}
