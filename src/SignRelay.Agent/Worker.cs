using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SignRelay.Agent.Options;
using SignRelay.Contracts;

namespace SignRelay.Agent;

public sealed class Worker : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IOptions<AgentOptions> _opt;
    private readonly SignToolRunner _signTool;
    private readonly IJobStaging _jobStaging;
    private readonly InteractiveUserProcessLauncher _interactive;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<Worker> _log;

    public Worker(
        IOptions<AgentOptions> opt,
        SignToolRunner signTool,
        IJobStaging jobStaging,
        InteractiveUserProcessLauncher interactive,
        IHttpClientFactory httpFactory,
        IHostApplicationLifetime lifetime,
        ILogger<Worker> log)
    {
        _opt = opt;
        _signTool = signTool;
        _jobStaging = jobStaging;
        _interactive = interactive;
        _httpFactory = httpFactory;
        _lifetime = lifetime;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opt = _opt.Value;

        if (opt.SigningExecution == SigningExecutionMode.InteractiveUser && !OperatingSystem.IsWindows())
        {
            _log.LogError("SigningExecution=InteractiveUser is only supported on Windows. The agent will not start.");
            _lifetime.StopApplication();
            return;
        }

        var interactive = SigningExecutionHelper.UseInteractiveSigning(opt)
                          && OperatingSystem.IsWindows()
                          && RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var extraDirs = SignToolSearchPaths.Build(interactive ? _interactive : null);

        _log.LogInformation(
            "SignRelay Agent starting. AgentId={AgentId}, Mode={Mode}, RelayUrl={RelayUrl}, SignTool={SignTool}.",
            opt.AgentId ?? "(none)",
            opt.SigningExecution,
            opt.RelayUrl,
            SignToolCommandBuilder.DescribeResolution(opt.SignToolPath, extraDirs));

        // Use IHttpClientFactory for base address + agent auth; per-job calls get their own auth header
        using var agentHttp = _httpFactory.CreateClient("SignRelayAgent");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var leased = await TryLeaseAsync(agentHttp, stoppingToken).ConfigureAwait(false);
                if (leased is null)
                {
                    await Task.Delay(opt.PollInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await ProcessJobAsync(leased, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException ex) when ((int?)ex.StatusCode is 401 or 403)
            {
                _log.LogError(ex, "Authentication or authorisation failure — check AgentToken configuration. Agent stopping.");
                _lifetime.StopApplication();
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
        var opt = _opt.Value;
        var body = JsonSerializer.Serialize(new WorkerLeaseRequest { AgentId = opt.AgentId }, Json);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync(ApiRoutes.WorkerLease, content, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent)
            return null;

        if (!resp.IsSuccessStatusCode)
        {
            var details = await HttpFailureDetails.FromResponseAsync("lease", 1, 1, resp, ct).ConfigureAwait(false);
            _log.LogError("Lease request failed.\n{Details}", details);
            resp.EnsureSuccessStatusCode();
        }

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var lease = JsonSerializer.Deserialize<LeaseResponse>(json, Json);

        if (lease is null || string.IsNullOrEmpty(lease.JobId) || string.IsNullOrEmpty(lease.LeaseToken))
        {
            _log.LogError("Received malformed lease response from server.");
            return null;
        }

        if (lease.Manifest?.Files is not { Count: > 0 })
        {
            _log.LogError("Lease for job {JobId} has an empty or missing manifest.", lease.JobId);
            return null;
        }

        if (!LeaseDownloadPath.TryValidate(lease.JobId, lease.UnsignedDownloadPaths, lease.Manifest.Files.Count, out var pathError))
        {
            _log.LogError("Lease for job {JobId}: {Error}", lease.JobId, pathError);
            return null;
        }

        _log.LogInformation("Leased job {JobId} ({FileCount} file(s)).", lease.JobId, lease.Manifest.Files.Count);
        return lease;
    }

    private async Task ProcessJobAsync(LeaseResponse lease, CancellationToken ct)
    {
        var opt = _opt.Value;
        var tempRoot = _jobStaging.GetJobDirectory(lease.JobId, opt);
        Directory.CreateDirectory(tempRoot);
        _jobStaging.EnsureInteractiveUserCanAccessJobDirectory(tempRoot, opt);

        // Build a per-job HttpClient that uses the lease token for all job-scoped calls
        using var jobHttp = _httpFactory.CreateClient("SignRelayJob");
        jobHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", lease.LeaseToken);

        try
        {
            for (var i = 0; i < lease.Manifest.Files.Count; i++)
            {
                var entry = lease.Manifest.Files[i];

                // Validate and normalize relative path before writing to disk
                string normalizedRel;
                try
                {
                    normalizedRel = PathSafety.NormalizeRelativePath(entry.RelativePath);
                }
                catch (InvalidOperationException ex)
                {
                    await FailRemoteAsync(jobHttp, lease.JobId, $"Invalid path in manifest: {ex.Message}", ct).ConfigureAwait(false);
                    return;
                }

                var dest = Path.GetFullPath(Path.Combine(tempRoot, normalizedRel));
                if (!PathSafety.IsUnderRoot(dest, tempRoot))
                {
                    await FailRemoteAsync(jobHttp, lease.JobId, $"Path escape detected for '{entry.RelativePath}'.", ct).ConfigureAwait(false);
                    return;
                }

                var url = lease.UnsignedDownloadPaths[i].TrimStart('/');
                try
                {
                    await HttpTransfer.DownloadToFileAsync(
                            jobHttp,
                            url,
                            dest,
                            $"unsigned download [{i}] {entry.RelativePath}",
                            ct,
                            details => _log.LogWarning("Job {JobId} transfer: {Details}", lease.JobId, details))
                        .ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    var details = ex.Message;
                    _log.LogError("Job {JobId} unsigned download failed.\n{Details}", lease.JobId, details);
                    await FailRemoteAsync(jobHttp, lease.JobId, HttpFailureDetails.Persist(details), ct).ConfigureAwait(false);
                    return;
                }

                var exit = await _signTool.SignAsync(
                        opt.SignToolPath,
                        dest,
                        opt.CertificateThumbprint,
                        opt.CertificateSubjectName,
                        opt.TimestampServerUrl,
                        entry.SignToolExtraArgs,
                        ct)
                    .ConfigureAwait(false);

                if (exit != 0)
                {
                    await FailRemoteAsync(jobHttp, lease.JobId, $"signtool exited with code {exit}", ct).ConfigureAwait(false);
                    return;
                }

                // Heartbeat after each file to keep the lease alive on multi-file jobs
                await SendHeartbeatAsync(jobHttp, lease.JobId, ct).ConfigureAwait(false);
            }

            try
            {
                using var uploaded = await HttpTransfer.SendWithRetryAsync(
                        jobHttp,
                        () =>
                        {
                            var mp = BuildSignedUpload(tempRoot, lease.Manifest);
                            return new HttpRequestMessage(HttpMethod.Post, ApiRoutes.WorkerSigned(lease.JobId))
                            {
                                Content = mp
                            };
                        },
                        $"signed upload ({lease.Manifest.Files.Count} file(s))",
                        ct,
                        details => _log.LogWarning("Job {JobId} transfer: {Details}", lease.JobId, details))
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                _log.LogError("Job {JobId} signed upload failed.\n{Details}", lease.JobId, ex.Message);
                await FailRemoteAsync(jobHttp, lease.JobId, HttpFailureDetails.Persist(ex.Message), ct).ConfigureAwait(false);
                return;
            }

            using var completeBody = new StringContent("{}", Encoding.UTF8, "application/json");
            using var done = await jobHttp.PostAsync(ApiRoutes.WorkerComplete(lease.JobId), completeBody, ct).ConfigureAwait(false);
            if (!done.IsSuccessStatusCode)
            {
                var details = await HttpFailureDetails.FromResponseAsync(
                        "complete", 1, 1, done, ct)
                    .ConfigureAwait(false);
                _log.LogError("Job {JobId} complete failed.\n{Details}", lease.JobId, details);
                await FailRemoteAsync(jobHttp, lease.JobId, HttpFailureDetails.Persist(details), ct).ConfigureAwait(false);
                return;
            }

            _log.LogInformation("Completed job {JobId}", lease.JobId);
        }
        catch (OperationCanceledException)
        {
            // Service stopping — attempt to fail the job on the server so it can be requeued
            try
            {
                using var cancelCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await FailRemoteAsync(jobHttp, lease.JobId, "Agent stopped during signing.", cancelCts.Token).ConfigureAwait(false);
            }
            catch
            {
                // best-effort
            }
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error processing job {JobId}.\n{Details}", lease.JobId, ex.Message);
            await FailRemoteAsync(jobHttp, lease.JobId, HttpFailureDetails.Persist(ex.Message), ct).ConfigureAwait(false);
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

    private static MultipartFormDataContent BuildSignedUpload(string tempRoot, JobManifestDto manifest)
    {
        var mp = new MultipartFormDataContent();
        for (var i = 0; i < manifest.Files.Count; i++)
        {
            var entry = manifest.Files[i];
            var normalizedRel = PathSafety.NormalizeRelativePath(entry.RelativePath);
            var path = Path.GetFullPath(Path.Combine(tempRoot, normalizedRel));
            var stream = File.OpenRead(path);
            var fileContent = new StreamContent(stream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            mp.Add(fileContent, $"file_{i}", Path.GetFileName(path));
        }

        return mp;
    }

    private async Task FailRemoteAsync(HttpClient http, string jobId, string error, CancellationToken ct)
    {
        _log.LogError("Job {JobId} failed: {Error}", jobId, error);
        try
        {
            var body = JsonSerializer.Serialize(new WorkerFailRequest { Error = error }, Json);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(ApiRoutes.WorkerFail(jobId), content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var details = await HttpFailureDetails.FromResponseAsync("fail notify", 1, 1, resp, ct)
                    .ConfigureAwait(false);
                _log.LogWarning("FailRemote for job {JobId}:\n{Details}", jobId, details);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not notify server of failure for job {JobId}.", jobId);
        }
    }

    private async Task SendHeartbeatAsync(HttpClient http, string jobId, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(ApiRoutes.WorkerHeartbeat(jobId), content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var details = await HttpFailureDetails.FromResponseAsync("heartbeat", 1, 1, resp, ct)
                    .ConfigureAwait(false);
                _log.LogDebug("Heartbeat for job {JobId}:\n{Details}", jobId, details);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Heartbeat for job {JobId} failed (non-fatal).", jobId);
        }
    }
}
