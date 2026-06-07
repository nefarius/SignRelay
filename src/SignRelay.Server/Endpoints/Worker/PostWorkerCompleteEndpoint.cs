using System.Security.Claims;
using FastEndpoints;
using SignRelay.Contracts;
using SignRelay.Server.Auth;
using SignRelay.Server.Services;

namespace SignRelay.Server.Api.Worker;

public sealed class PostWorkerCompleteEndpoint : Endpoint<WorkerCompleteRequest>
{
    private readonly JobService _jobs;

    public PostWorkerCompleteEndpoint(JobService jobs) => _jobs = jobs;

    public override void Configure()
    {
        Post($"{SignRelay.Contracts.ApiRoutes.Prefix}/worker/jobs/{{jobId}}/complete");
        AuthSchemes(SignRelayAuthenticationHandler.SchemeName);
        Policies("Lease");
    }

    public override async Task HandleAsync(WorkerCompleteRequest req, CancellationToken ct)
    {
        var jobId = Route<string>("jobId")!;
        var claimedJobId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (claimedJobId != jobId)
        {
            await Send.ForbiddenAsync(ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await _jobs.CompleteJobAsync(jobId, ct).ConfigureAwait(false);
            await Send.OkAsync(ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // Business-rule violation (e.g. missing signed artifacts): fail the job
            await _jobs.FailJobAsync(jobId, ex.Message, ct).ConfigureAwait(false);
            AddError(ex.Message);
            await Send.ErrorsAsync(400, ct).ConfigureAwait(false);
        }
        // Let unexpected exceptions (infra failures) propagate as 500 — job stays leased for retry
    }
}
