using System.Security.Claims;
using FastEndpoints;
using SignRelay.Contracts;
using SignRelay.Server.Auth;
using SignRelay.Server.Services;

namespace SignRelay.Server.Api.Worker;

public sealed class GetWorkerUnsignedFileEndpoint : EndpointWithoutRequest
{
    private readonly JobService _jobs;

    public GetWorkerUnsignedFileEndpoint(JobService jobs) => _jobs = jobs;

    public override void Configure()
    {
        Get($"{SignRelay.Contracts.ApiRoutes.Prefix}/worker/jobs/{{jobId}}/unsigned/{{fileName}}");
        AuthSchemes(SignRelayAuthenticationHandler.SchemeName);
        Policies("Lease");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var jobId = Route<string>("jobId")!;
        var fileName = Route<string>("fileName")!;

        // The Lease policy ensures the caller holds a lease token bound to a specific job.
        // Enforce that the route jobId matches the claim.
        var claimedJobId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (claimedJobId != jobId)
        {
            await Send.ForbiddenAsync(ct).ConfigureAwait(false);
            return;
        }

        await using var stream = await _jobs.OpenUnsignedAsync(jobId, fileName, ct).ConfigureAwait(false);
        if (stream is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.StreamAsync(stream, fileName, null, "application/octet-stream", null, false, ct).ConfigureAwait(false);
    }
}
