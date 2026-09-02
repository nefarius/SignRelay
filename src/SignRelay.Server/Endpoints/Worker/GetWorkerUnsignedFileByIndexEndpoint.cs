using System.Security.Claims;
using FastEndpoints;
using SignRelay.Contracts;
using SignRelay.Server.Api;
using SignRelay.Server.Auth;
using SignRelay.Server.Services;

namespace SignRelay.Server.Api.Worker;

public sealed class GetWorkerUnsignedFileByIndexEndpoint : EndpointWithoutRequest
{
    private readonly JobService _jobs;
    private readonly ILogger<GetWorkerUnsignedFileByIndexEndpoint> _log;

    public GetWorkerUnsignedFileByIndexEndpoint(JobService jobs, ILogger<GetWorkerUnsignedFileByIndexEndpoint> log)
    {
        _jobs = jobs;
        _log = log;
    }

    public override void Configure()
    {
        Get($"{ApiRoutes.Prefix}/worker/jobs/{{jobId}}/files/{{index}}/unsigned");
        AuthSchemes(SignRelayAuthenticationHandler.SchemeName);
        Policies("Lease");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var jobIdRaw = Route<string>("jobId");
        if (!JobRoute.TryBind(jobIdRaw, out var jobId))
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

        var index = Route<int>("index");
        try
        {
            var opened = await _jobs.OpenUnsignedByIndexAsync(jobId, index, ct).ConfigureAwait(false);
            if (opened is null)
            {
                ServerHttpError.Log(_log, HttpContext, 404, "Unsigned file not found.");
                await Send.NotFoundAsync(ct).ConfigureAwait(false);
                return;
            }

            var (stream, fileName) = opened.Value;
            await using (stream)
            {
                await Send.StreamAsync(stream, fileName, null, "application/octet-stream", null, false, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException ex)
        {
            AddError(ex.Message);
            ServerHttpError.Log(_log, HttpContext, 400, ex.Message);
            await Send.ErrorsAsync(400, ct).ConfigureAwait(false);
        }
    }
}
