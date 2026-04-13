using FastEndpoints;
using SignRelay.Contracts;
using SignRelay.Server.Auth;
using SignRelay.Server.Services;

namespace SignRelay.Server.Api.Worker;

public sealed class PostWorkerFailEndpoint : Endpoint<WorkerFailRequest>
{
    private readonly JobService _jobs;

    public PostWorkerFailEndpoint(JobService jobs) => _jobs = jobs;

    public override void Configure()
    {
        Post($"{SignRelay.Contracts.ApiRoutes.Prefix}/worker/jobs/{{jobId}}/fail");
        AuthSchemes(SignRelayAuthenticationHandler.SchemeName);
        Policies("Agent");
    }

    public override async Task HandleAsync(WorkerFailRequest req, CancellationToken ct)
    {
        var jobId = Route<string>("jobId")!;
        await _jobs.FailJobAsync(jobId, req.Error, ct).ConfigureAwait(false);
        await SendOkAsync(ct).ConfigureAwait(false);
    }
}
