using System.Text.Json;
using FastEndpoints;
using SignRelay.Contracts;
using SignRelay.Server.Api;
using SignRelay.Server.Auth;
using SignRelay.Server.Services;

namespace SignRelay.Server.Api.Ci;

public sealed class PostSubmitJobEndpoint : EndpointWithoutRequest<SubmitJobResponse>
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    private readonly JobService _jobs;

    public PostSubmitJobEndpoint(JobService jobs) => _jobs = jobs;

    public override void Configure()
    {
        Post(SignRelay.Contracts.ApiRoutes.Jobs);
        AuthSchemes(SignRelayAuthenticationHandler.SchemeName);
        Policies("Ci");
        AllowFileUploads();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!HttpContext.Request.HasFormContentType)
        {
            AddError("Request must be multipart/form-data.");
            await Send.ErrorsAsync(400, ct).ConfigureAwait(false);
            return;
        }

        var manifestJson = HttpContext.Request.Form["manifest"].ToString();
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            AddError("Missing form field 'manifest'.");
            await Send.ErrorsAsync(400, ct).ConfigureAwait(false);
            return;
        }

        JobManifestDto? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<JobManifestDto>(manifestJson, Json);
        }
        catch (JsonException)
        {
            AddError("Manifest is not valid JSON.");
            await Send.ErrorsAsync(400, ct).ConfigureAwait(false);
            return;
        }

        if (manifest?.Files is not { Count: > 0 })
        {
            AddError("Manifest must contain at least one file entry.");
            await Send.ErrorsAsync(400, ct).ConfigureAwait(false);
            return;
        }

        var formFiles = new List<IFormFile>();
        for (var i = 0; i < manifest.Files.Count; i++)
        {
            var f = HttpContext.Request.Form.Files.GetFile($"file_{i}");
            if (f is null || f.Length == 0)
            {
                AddError($"Missing non-empty form file 'file_{i}'.");
                await Send.ErrorsAsync(400, ct).ConfigureAwait(false);
                return;
            }

            formFiles.Add(f);
        }

        var inputs = new List<(string RelativePath, Stream Content, long Length)>();
        for (var i = 0; i < manifest.Files.Count; i++)
        {
            var f = formFiles[i];
            inputs.Add((manifest.Files[i].RelativePath, f.OpenReadStream(), f.Length));
        }

        try
        {
            var (job, token) = await _jobs.CreateJobAsync(manifest, inputs, ct).ConfigureAwait(false);
            foreach (var (_, s, _) in inputs)
                await s.DisposeAsync().ConfigureAwait(false);

            await Send.OkAsync(
                    new SubmitJobResponse
                    {
                        JobId = job.Id,
                        JobToken = token,
                        ExpiresAtUtc = job.ExpiresUtc
                    },
                    ct)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            foreach (var (_, s, _) in inputs)
                await s.DisposeAsync().ConfigureAwait(false);

            // Validation / business errors are 400
            AddError(ex.Message);
            await Send.ErrorsAsync(400, ct).ConfigureAwait(false);
        }
        catch
        {
            foreach (var (_, s, _) in inputs)
                await s.DisposeAsync().ConfigureAwait(false);

            // Infrastructure failures are 500 with a generic message
            AddError("An internal error occurred. Please try again.");
            await Send.ErrorsAsync(500, ct).ConfigureAwait(false);
        }
    }
}
