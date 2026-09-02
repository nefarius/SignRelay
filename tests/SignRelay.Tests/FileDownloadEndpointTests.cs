using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SignRelay.Contracts;

namespace SignRelay.Tests;

public sealed class FileDownloadEndpointTests : IAsyncLifetime
{
    private readonly SignRelayApiFactory _factory = new();
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Indexed_unsigned_and_signed_round_trip_dshidmini_paths()
    {
        using var ci = _factory.CreateClient();
        ci.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.CiToken);

        using var form = new MultipartFormDataContent();
        var manifest = new JobManifestDto
        {
            Files =
            [
                new JobFileEntry { RelativePath = DsHidMiniSigningFixture.X64RelativePath },
                new JobFileEntry { RelativePath = DsHidMiniSigningFixture.Arm64RelativePath }
            ]
        };
        form.Add(new StringContent(JsonSerializer.Serialize(manifest, Json), Encoding.UTF8, "application/json"), "manifest");
        form.Add(new ByteArrayContent(DsHidMiniSigningFixture.X64Bytes), "file_0", "dshidmini.dll");
        form.Add(new ByteArrayContent(DsHidMiniSigningFixture.Arm64Bytes), "file_1", "dshidmini.dll");

        using var submit = await ci.PostAsync(ApiRoutes.Jobs, form);
        submit.EnsureSuccessStatusCode();
        var submitBody = await submit.Content.ReadFromJsonAsync<SubmitJobResponse>(Json);
        Assert.NotNull(submitBody);
        Assert.NotNull(submitBody.SignedDownloadPaths);
        Assert.Equal(2, submitBody.SignedDownloadPaths.Count);
        Assert.All(submitBody.SignedDownloadPaths, p =>
        {
            Assert.DoesNotContain("%2F", p, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("%5C", p, StringComparison.OrdinalIgnoreCase);
        });

        using var agent = _factory.CreateClient();
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.AgentToken);
        using var leaseContent = new StringContent("{}", Encoding.UTF8, "application/json");
        using var leaseResp = await agent.PostAsync(ApiRoutes.WorkerLease, leaseContent);
        leaseResp.EnsureSuccessStatusCode();
        var lease = await leaseResp.Content.ReadFromJsonAsync<LeaseResponse>(Json);
        Assert.NotNull(lease);
        Assert.True(LeaseDownloadPath.TryValidate(lease.JobId, lease.UnsignedDownloadPaths, 2, out _));

        using var job = _factory.CreateClient();
        job.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", lease.LeaseToken);

        using var u0 = await job.GetAsync(lease.UnsignedDownloadPaths[0].TrimStart('/'));
        using var u1 = await job.GetAsync(lease.UnsignedDownloadPaths[1].TrimStart('/'));
        Assert.Equal(HttpStatusCode.OK, u0.StatusCode);
        Assert.Equal(HttpStatusCode.OK, u1.StatusCode);
        Assert.Equal(DsHidMiniSigningFixture.X64Bytes, await u0.Content.ReadAsByteArrayAsync());
        Assert.Equal(DsHidMiniSigningFixture.Arm64Bytes, await u1.Content.ReadAsByteArrayAsync());

        using var signedForm = new MultipartFormDataContent();
        signedForm.Add(new ByteArrayContent(DsHidMiniSigningFixture.X64SignedBytes), "file_0", "dshidmini.dll");
        signedForm.Add(new ByteArrayContent(DsHidMiniSigningFixture.Arm64SignedBytes), "file_1", "dshidmini.dll");
        using var upload = await job.PostAsync(ApiRoutes.WorkerSigned(lease.JobId), signedForm);
        upload.EnsureSuccessStatusCode();
        using var complete = await job.PostAsync(
            ApiRoutes.WorkerComplete(lease.JobId),
            new StringContent("{}", Encoding.UTF8, "application/json"));
        complete.EnsureSuccessStatusCode();

        using var jobCi = _factory.CreateClient();
        jobCi.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", submitBody.JobToken);
        using var s0 = await jobCi.GetAsync(submitBody.SignedDownloadPaths[0].TrimStart('/'));
        using var s1 = await jobCi.GetAsync(submitBody.SignedDownloadPaths[1].TrimStart('/'));
        Assert.Equal(HttpStatusCode.OK, s0.StatusCode);
        Assert.Equal(HttpStatusCode.OK, s1.StatusCode);
        Assert.Equal(DsHidMiniSigningFixture.X64SignedBytes, await s0.Content.ReadAsByteArrayAsync());
        Assert.Equal(DsHidMiniSigningFixture.Arm64SignedBytes, await s1.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Invalid_job_id_on_signed_download_is_400()
    {
        using var ci = _factory.CreateClient();
        ci.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.CiToken);
        using var resp = await ci.GetAsync("/api/v1/jobs/not-a-job-id/files/0/signed");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(body));
    }

    [Fact]
    public async Task Out_of_range_index_is_404()
    {
        using var ci = _factory.CreateClient();
        ci.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.CiToken);
        using var form = new MultipartFormDataContent();
        var manifest = new JobManifestDto
        {
            Files = [new JobFileEntry { RelativePath = "file.exe" }]
        };
        form.Add(new StringContent(JsonSerializer.Serialize(manifest, Json), Encoding.UTF8, "application/json"), "manifest");
        form.Add(new ByteArrayContent([0x4D, 0x5A]), "file_0", "file.exe");
        using var submit = await ci.PostAsync(ApiRoutes.Jobs, form);
        submit.EnsureSuccessStatusCode();
        var body = await submit.Content.ReadFromJsonAsync<SubmitJobResponse>(Json);
        Assert.NotNull(body);

        using var jobCi = _factory.CreateClient();
        jobCi.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.JobToken);
        using var missing = await jobCi.GetAsync(ApiRoutes.JobSignedFileByIndex(body.JobId, 9).TrimStart('/'));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
