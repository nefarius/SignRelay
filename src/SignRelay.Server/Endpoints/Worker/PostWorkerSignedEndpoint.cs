using System.Security.Claims;
using FastEndpoints;
using SignRelay.Server.Auth;
using SignRelay.Server.Services;

namespace SignRelay.Server.Api.Worker;

public sealed class PostWorkerSignedEndpoint : EndpointWithoutRequest
{
    private readonly JobService _jobs;

    public PostWorkerSignedEndpoint(JobService jobs) => _jobs = jobs;

    public override void Configure()
    {
        Post($"{SignRelay.Contracts.ApiRoutes.Prefix}/worker/jobs/{{jobId}}/signed");
        AuthSchemes(SignRelayAuthenticationHandler.SchemeName);
        Policies("Lease");
        AllowFileUploads();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var jobId = Route<string>("jobId")!;
        var claimedJobId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (claimedJobId != jobId)
        {
            await Send.ForbiddenAsync(ct).ConfigureAwait(false);
            return;
        }

        if (!HttpContext.Request.HasFormContentType)
        {
            AddError("Request must be multipart/form-data.");
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
            // Business-rule violations: 400, leave job in Signing for potential retry
            AddError(ex.Message);
            await Send.ErrorsAsync(400, ct).ConfigureAwait(false);
        }
    }
}
