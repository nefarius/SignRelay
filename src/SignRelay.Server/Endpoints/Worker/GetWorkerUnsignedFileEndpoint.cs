using FastEndpoints;
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
        Policies("Agent");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var jobId = Route<string>("jobId")!;
        var fileName = Route<string>("fileName")!;

        await using var stream = await _jobs.OpenUnsignedAsync(jobId, fileName, ct).ConfigureAwait(false);
        if (stream is null)
        {
            await SendNotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await SendStreamAsync(stream, fileName, null, "application/octet-stream", null, false, ct).ConfigureAwait(false);
    }
}
