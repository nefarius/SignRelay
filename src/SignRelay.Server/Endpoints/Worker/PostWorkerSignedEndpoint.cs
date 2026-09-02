using System.Security.Claims;
using FastEndpoints;
using SignRelay.Contracts;
using SignRelay.Server.Api;
using SignRelay.Server.Auth;
using SignRelay.Server.Services;

namespace SignRelay.Server.Api.Worker;

public sealed class PostWorkerSignedEndpoint : EndpointWithoutRequest
{
    private readonly JobService _jobs;
    private readonly ILogger<PostWorkerSignedEndpoint> _log;

    public PostWorkerSignedEndpoint(JobService jobs, ILogger<PostWorkerSignedEndpoint> log)
    {
        _jobs = jobs;
        _log = log;
    }

    public override void Configure()
    {
        Post($"{ApiRoutes.Prefix}/worker/jobs/{{jobId}}/signed");
        AuthSchemes(SignRelayAuthenticationHandler.SchemeName);
        Policies("Lease");
        AllowFileUploads();
    }

    public override async Task HandleAsync(CancellationToken ct)
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

        if (!HttpContext.Request.HasFormContentType)
        {
            AddError("Request must be multipart/form-data.");
            ServerHttpError.Log(_log, HttpContext, 400, "Request must be multipart/form-data.");
            await Send.ErrorsAsync(400, ct).ConfigureAwait(false);
            return;
        }

        var files = new List<IFormFile>();
        for (var i = 0;; i++)
        {
            var f = HttpContext.Request.Form.Files.GetFile($"file_{i}");
            if (f is null)
                break;
            files.Add(f);
        }

        if (files.Count == 0)
        {
            AddError("Expected one or more form files named file_0, file_1, ...");
            ServerHttpError.Log(_log, HttpContext, 400, "Expected one or more form files named file_0, file_1, ...");
            await Send.ErrorsAsync(400, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await _jobs.SaveSignedFilesAsync(jobId, files, ct).ConfigureAwait(false);
            await Send.OkAsync(ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            AddError(ex.Message);
            ServerHttpError.Log(_log, HttpContext, 400, ex.Message);
            await Send.ErrorsAsync(400, ct).ConfigureAwait(false);
        }
    }
}
