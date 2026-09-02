using System.Security.Claims;
using FastEndpoints;
using SignRelay.Contracts;
using SignRelay.Server.Api;
using SignRelay.Server.Auth;
using SignRelay.Server.Services;

namespace SignRelay.Server.Api.Worker;

public sealed class PostWorkerCompleteEndpoint : Endpoint<WorkerCompleteRequest>
{
    private readonly JobService _jobs;
    private readonly ILogger<PostWorkerCompleteEndpoint> _log;

    public PostWorkerCompleteEndpoint(JobService jobs, ILogger<PostWorkerCompleteEndpoint> log)
    {
        _jobs = jobs;
        _log = log;
    }

    public override void Configure()
    {
        Post($"{ApiRoutes.Prefix}/worker/jobs/{{jobId}}/complete");
        AuthSchemes(SignRelayAuthenticationHandler.SchemeName);
        Policies("Lease");
    }

    public override async Task HandleAsync(WorkerCompleteRequest req, CancellationToken ct)
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

        try
        {
            await _jobs.CompleteJobAsync(jobId, ct).ConfigureAwait(false);
            await Send.OkAsync(ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            await _jobs.FailJobAsync(jobId, ex.Message, ct).ConfigureAwait(false);
            AddError(ex.Message);
            ServerHttpError.Log(_log, HttpContext, 400, ex.Message);
            await Send.ErrorsAsync(400, ct).ConfigureAwait(false);
        }
    }
}
