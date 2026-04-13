using FastEndpoints;
using SignRelay.Server.Auth;
using SignRelay.Server.Api;
using SignRelay.Server.Services;

namespace SignRelay.Server.Api.Ci;

public sealed class GetJobSignedFileEndpoint : EndpointWithoutRequest
{
    private readonly JobService _jobs;

    public GetJobSignedFileEndpoint(JobService jobs) => _jobs = jobs;

    public override void Configure()
    {
        Get($"{SignRelay.Contracts.ApiRoutes.Prefix}/jobs/{{id}}/signed/{{fileName}}");
        AuthSchemes(SignRelayAuthenticationHandler.SchemeName);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var id = Route<string>("id")!;
        var fileName = Route<string>("fileName")!;
        if (!JobAccess.CanAccessJob(User, id))
        {
            await Send.UnauthorizedAsync(ct).ConfigureAwait(false);
            return;
        }

        await using var stream = await _jobs.OpenSignedAsync(id, fileName, ct).ConfigureAwait(false);
        if (stream is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.StreamAsync(stream, fileName, null, "application/octet-stream", null, false, ct).ConfigureAwait(false);
    }
}
