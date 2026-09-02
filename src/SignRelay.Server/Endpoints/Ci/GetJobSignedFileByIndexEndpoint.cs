using FastEndpoints;
using SignRelay.Contracts;
using SignRelay.Server.Api;
using SignRelay.Server.Auth;
using SignRelay.Server.Services;

namespace SignRelay.Server.Api.Ci;

public sealed class GetJobSignedFileByIndexEndpoint : EndpointWithoutRequest
{
    private readonly JobService _jobs;
    private readonly ILogger<GetJobSignedFileByIndexEndpoint> _log;

    public GetJobSignedFileByIndexEndpoint(JobService jobs, ILogger<GetJobSignedFileByIndexEndpoint> log)
    {
        _jobs = jobs;
        _log = log;
    }

    public override void Configure()
    {
        Get($"{ApiRoutes.Prefix}/jobs/{{jobId}}/files/{{index}}/signed");
        AuthSchemes(SignRelayAuthenticationHandler.SchemeName);
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

        if (!JobAccess.CanAccessJob(User, jobId))
        {
            ServerHttpError.Log(_log, HttpContext, 401, "Not authorized for this job.");
            await Send.UnauthorizedAsync(ct).ConfigureAwait(false);
            return;
        }

        var index = Route<int>("index");
        try
        {
            var opened = await _jobs.OpenSignedByIndexAsync(jobId, index, ct).ConfigureAwait(false);
            if (opened is null)
            {
                ServerHttpError.Log(_log, HttpContext, 404, "Signed file not found.");
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
