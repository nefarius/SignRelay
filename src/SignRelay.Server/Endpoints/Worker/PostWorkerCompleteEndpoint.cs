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
        Policies("Agent");
    }

    public override async Task HandleAsync(WorkerCompleteRequest req, CancellationToken ct)
    {
        var jobId = Route<string>("jobId")!;
        try
        {
            await _jobs.CompleteJobAsync(jobId, ct).ConfigureAwait(false);
            await Send.OkAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _jobs.FailJobAsync(jobId, ex.Message, ct).ConfigureAwait(false);
            AddError(ex.Message);
            await Send.ErrorsAsync(400, ct).ConfigureAwait(false);
        }
    }
}
