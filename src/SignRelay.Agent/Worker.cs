using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SignRelay.Agent.Options;
using SignRelay.Contracts;

namespace SignRelay.Agent;

public sealed class Worker : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    private readonly IOptions<AgentOptions> _opt;
    private readonly SignToolRunner _signTool;
    private readonly IJobStaging _jobStaging;
    private readonly ILogger<Worker> _log;

    public Worker(IOptions<AgentOptions> opt, SignToolRunner signTool, IJobStaging jobStaging, ILogger<Worker> log)
    {
        _opt = opt;
        _signTool = signTool;
        _jobStaging = jobStaging;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opt = _opt.Value;
        if (string.IsNullOrWhiteSpace(opt.AgentToken))
        {
            _log.LogError("AgentToken is not configured.");
            return;
        }

        var http = new HttpClient { BaseAddress = new Uri(opt.RelayUrl.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", opt.AgentToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var leased = await TryLeaseAsync(http, stoppingToken).ConfigureAwait(false);
                if (leased is null)
                {
                    await Task.Delay(opt.PollInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await ProcessJobAsync(http, leased, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Agent loop error.");
                await Task.Delay(opt.PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<LeaseResponse?> TryLeaseAsync(HttpClient http, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new WorkerLeaseRequest { AgentId = _opt.Value.AgentId }, Json);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync("api/v1/worker/lease", content, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent)
            return null;

        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<LeaseResponse>(json, Json);
    }

    private async Task ProcessJobAsync(HttpClient http, LeaseResponse lease, CancellationToken ct)
    {
        var opt = _opt.Value;
        var tempRoot = _jobStaging.GetJobDirectory(lease.JobId, opt);
        Directory.CreateDirectory(tempRoot);
        _jobStaging.EnsureInteractiveUserCanAccessJobDirectory(tempRoot, opt);

        try
        {
            for (var i = 0; i < lease.Manifest.Files.Count; i++)
            {
                var entry = lease.Manifest.Files[i];
                var url = lease.UnsignedDownloadPaths[i].TrimStart('/');
                using var get = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                get.EnsureSuccessStatusCode();

                var dest = Path.Combine(tempRoot, entry.RelativePath.Replace('\\', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                await using (var fs = File.Create(dest))
                {
                    await get.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
                }

                var exit = await _signTool.SignAsync(
                        opt.SignToolPath,
                        dest,
                        opt.CertificateThumbprint,
                        opt.TimestampServerUrl,
                        entry.SignToolExtraArgs,
                        ct)
                    .ConfigureAwait(false);

                if (exit != 0)
                {
                    await FailRemoteAsync(http, lease.JobId, $"signtool exited with code {exit}", ct).ConfigureAwait(false);
                    return;
                }
            }

            using var mp = new MultipartFormDataContent();
            for (var i = 0; i < lease.Manifest.Files.Count; i++)
            {
                var entry = lease.Manifest.Files[i];
                var path = Path.Combine(tempRoot, entry.RelativePath.Replace('\\', Path.DirectorySeparatorChar));
                var stream = File.OpenRead(path);
                var fileContent = new StreamContent(stream);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                mp.Add(fileContent, $"file_{i}", Path.GetFileName(path));
            }

            using (var put = await http.PostAsync($"api/v1/worker/jobs/{lease.JobId}/signed", mp, ct).ConfigureAwait(false))
            {
                if (!put.IsSuccessStatusCode)
                {
                    var err = await put.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    await FailRemoteAsync(http, lease.JobId, $"Upload failed: {(int)put.StatusCode} {err}", ct).ConfigureAwait(false);
                    return;
                }
            }

            using var completeBody = new StringContent("{}", Encoding.UTF8, "application/json");
            using var done = await http.PostAsync($"api/v1/worker/jobs/{lease.JobId}/complete", completeBody, ct).ConfigureAwait(false);
            if (!done.IsSuccessStatusCode)
            {
                var err = await done.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                await FailRemoteAsync(http, lease.JobId, $"Complete failed: {(int)done.StatusCode} {err}", ct).ConfigureAwait(false);
                return;
            }

            _log.LogInformation("Completed job {JobId}", lease.JobId);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not delete temp dir {Path}", tempRoot);
            }
        }
    }

    private async Task FailRemoteAsync(HttpClient http, string jobId, string error, CancellationToken ct)
    {
        _log.LogError("{Error}", error);
        var payload = JsonSerializer.Serialize(new WorkerFailRequest { Error = error }, Json);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync($"api/v1/worker/jobs/{jobId}/fail", content, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            _log.LogError("Could not report failure to relay: {Code}", (int)resp.StatusCode);
    }

}
