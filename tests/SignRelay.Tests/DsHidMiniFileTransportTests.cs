using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignRelay.Cli.Commands;
using SignRelay.Contracts;
using SignRelay.Server.Data;
using SignRelay.Server.Options;
using SignRelay.Server.Services;

namespace SignRelay.Tests;

/// <summary>
/// Regression for the DsHidMini nested-path 400:
/// https://github.com/nefarius/DsHidMini/actions/runs/33572516285/job/100075423146
/// </summary>
public sealed class DsHidMiniFileTransportTests : IDisposable
{
    private readonly string _storagePath;
    private readonly AppDbContext _db;
    private readonly JobEventHub _hub;
    private readonly JobService _jobs;

    public DsHidMiniFileTransportTests()
    {
        _storagePath = Path.Combine(Path.GetTempPath(), "signrelay-dshidmini-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_storagePath);

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_storagePath, "test.db")}")
            .Options;
        _db = new AppDbContext(dbOptions);
        _db.Database.EnsureCreated();
        _hub = new JobEventHub();
        _jobs = new JobService(
            _db,
            Options.Create(new SignRelayOptions
            {
                StoragePath = _storagePath,
                JobTimeToLive = TimeSpan.FromHours(1),
                LeaseDuration = TimeSpan.FromMinutes(30),
                MaxLeaseAttempts = 3,
            }),
            _hub,
            NullLogger<JobService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_storagePath, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Submit_lease_index_download_upload_complete_preserves_same_basename_files()
    {
        var (job, _) = await CreateDsHidMiniJobAsync();
        var lease = await _jobs.TryLeaseAsync("agent-1", CancellationToken.None);
        Assert.NotNull(lease);
        Assert.Equal(2, lease.UnsignedDownloadPaths.Count);
        foreach (var url in lease.UnsignedDownloadPaths)
        {
            Assert.DoesNotContain("%2F", url, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("%5C", url, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith($"/api/v1/worker/jobs/{job.Id}/files/", url);
            Assert.EndsWith("/unsigned", url);
        }

        var u0 = await _jobs.OpenUnsignedByIndexAsync(job.Id, 0, CancellationToken.None);
        var u1 = await _jobs.OpenUnsignedByIndexAsync(job.Id, 1, CancellationToken.None);
        Assert.NotNull(u0);
        Assert.NotNull(u1);
        Assert.Equal("dshidmini.dll", u0.Value.FileName);
        Assert.Equal("dshidmini.dll", u1.Value.FileName);
        Assert.Equal(DsHidMiniSigningFixture.X64Bytes, await ReadAllAsync(u0.Value.Stream));
        Assert.Equal(DsHidMiniSigningFixture.Arm64Bytes, await ReadAllAsync(u1.Value.Stream));

        var signed = new IFormFile[]
        {
            new MemoryFormFile(DsHidMiniSigningFixture.X64SignedBytes, "file_0"),
            new MemoryFormFile(DsHidMiniSigningFixture.Arm64SignedBytes, "file_1")
        };
        await _jobs.SaveSignedFilesAsync(job.Id, signed, CancellationToken.None);
        await _jobs.CompleteJobAsync(job.Id, CancellationToken.None);

        var s0 = await _jobs.OpenSignedByIndexAsync(job.Id, 0, CancellationToken.None);
        var s1 = await _jobs.OpenSignedByIndexAsync(job.Id, 1, CancellationToken.None);
        Assert.NotNull(s0);
        Assert.NotNull(s1);
        Assert.Equal(DsHidMiniSigningFixture.X64SignedBytes, await ReadAllAsync(s0.Value.Stream));
        Assert.Equal(DsHidMiniSigningFixture.Arm64SignedBytes, await ReadAllAsync(s1.Value.Stream));

        var signedUrls = _jobs.SignedDownloadPaths(job.Id, 2);
        foreach (var url in signedUrls)
        {
            Assert.DoesNotContain("%2F", url, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("%5C", url, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Legacy_encoded_path_is_rejected_by_proxy_like_handler_while_index_route_is_not()
    {
        var jobId = "9694ce9d0d174c1db0a5ccde585784b1";
        var legacy = ApiRoutes.WorkerUnsigned(jobId, DsHidMiniSigningFixture.X64RelativePath);
        var indexed = ApiRoutes.WorkerUnsignedByIndex(jobId, 0);
        Assert.Contains("%2F", legacy, StringComparison.OrdinalIgnoreCase);

        var inner = new SequenceHandler(req =>
        {
            if ((req.RequestUri?.OriginalString ?? "").Contains("/files/0/unsigned", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(DsHidMiniSigningFixture.X64Bytes),
                    RequestMessage = req
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            {
                Content = new StringContent("not reached"),
                RequestMessage = req
            };
        });

        using var http = new HttpClient(new EncodedSlashRejectingHandler(inner))
        {
            BaseAddress = new Uri("https://signrelay.api.nefarius.systems/")
        };

        var legacyEx = await Assert.ThrowsAsync<HttpRequestException>(() =>
            HttpTransfer.SendWithRetryAsync(
                http,
                () => new HttpRequestMessage(HttpMethod.Get, legacy.TrimStart('/')),
                $"unsigned download [0] {DsHidMiniSigningFixture.X64RelativePath}",
                CancellationToken.None,
                delayForAttempt: (_, _) => TimeSpan.Zero));
        Assert.Contains("400", legacyEx.Message);
        Assert.Contains("Body: (empty)", legacyEx.Message);

        using var indexedResp = await HttpTransfer.SendWithRetryAsync(
            http,
            () => new HttpRequestMessage(HttpMethod.Get, indexed.TrimStart('/')),
            $"unsigned download [0] {DsHidMiniSigningFixture.X64RelativePath}",
            CancellationToken.None,
            delayForAttempt: (_, _) => TimeSpan.Zero);
        Assert.True(indexedResp.IsSuccessStatusCode);
        var bytes = await indexedResp.Content.ReadAsByteArrayAsync();
        Assert.Equal(DsHidMiniSigningFixture.X64Bytes, bytes);
    }

    [Fact]
    public async Task Empty_400_and_bodied_400_are_persisted_on_the_job()
    {
        var (job, _) = await CreateDsHidMiniJobAsync();
        await _jobs.TryLeaseAsync("agent-1", CancellationToken.None);

        var emptyDetails = HttpFailureDetails.Format(
            $"unsigned download [0] {DsHidMiniSigningFixture.X64RelativePath}",
            1, 3, "GET",
            ApiRoutes.WorkerUnsigned(job.Id, DsHidMiniSigningFixture.X64RelativePath),
            400, "Bad Request", null, "", null);
        await _jobs.FailJobAsync(job.Id, emptyDetails, CancellationToken.None);

        var failed = await _jobs.GetJobAsync(job.Id, CancellationToken.None);
        Assert.Equal(JobStatus.Failed, failed!.Status);
        Assert.Contains("Body: (empty)", failed.ErrorMessage);
        Assert.Contains("400", failed.ErrorMessage);
        Assert.Equal(failed.ErrorMessage, _jobs.ToPayload(failed).Error);

        var (job2, _) = await CreateDsHidMiniJobAsync();
        await _jobs.TryLeaseAsync("agent-1", CancellationToken.None);
        var bodyDetails = HttpFailureDetails.Format(
            "unsigned download", 1, 1, "GET", "/x", 400, "Bad Request",
            ["Content-Type: application/json"],
            "{\"errors\":[\"Job id is invalid.\"]}",
            null);
        await _jobs.FailJobAsync(job2.Id, bodyDetails, CancellationToken.None);
        var failed2 = await _jobs.GetJobAsync(job2.Id, CancellationToken.None);
        Assert.Contains("Job id is invalid.", failed2!.ErrorMessage);
    }

    [Fact]
    public void Cli_prefers_indexed_signed_paths_from_submit_response()
    {
        var jobId = "9694ce9d0d174c1db0a5ccde585784b1";
        var submit = new SubmitJobResponse
        {
            JobId = jobId,
            JobToken = "token",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
            SignedDownloadPaths =
            [
                ApiRoutes.JobSignedFileByIndex(jobId, 0),
                ApiRoutes.JobSignedFileByIndex(jobId, 1)
            ]
        };

        var url0 = SubmitCommand.ResolveSignedDownloadUrl(submit, 0, DsHidMiniSigningFixture.X64RelativePath);
        Assert.Equal(ApiRoutes.JobSignedFileByIndex(jobId, 0).TrimStart('/'), url0);
        Assert.DoesNotContain("%2F", url0, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cli_falls_back_to_legacy_path_when_server_omits_signed_paths()
    {
        var jobId = "9694ce9d0d174c1db0a5ccde585784b1";
        var submit = new SubmitJobResponse
        {
            JobId = jobId,
            JobToken = "token",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
        };

        var url = SubmitCommand.ResolveSignedDownloadUrl(submit, 0, DsHidMiniSigningFixture.X64RelativePath);
        Assert.Contains("%2F", url, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(JobEntity Job, string Token)> CreateDsHidMiniJobAsync()
    {
        var manifest = new JobManifestDto
        {
            Files =
            [
                new JobFileEntry { RelativePath = DsHidMiniSigningFixture.X64RelativePath },
                new JobFileEntry { RelativePath = DsHidMiniSigningFixture.Arm64RelativePath }
            ]
        };
        var files = new List<(string RelativePath, Stream Content, long Length)>
        {
            (DsHidMiniSigningFixture.X64RelativePath, new MemoryStream(DsHidMiniSigningFixture.X64Bytes), DsHidMiniSigningFixture.X64Bytes.Length),
            (DsHidMiniSigningFixture.Arm64RelativePath, new MemoryStream(DsHidMiniSigningFixture.Arm64Bytes), DsHidMiniSigningFixture.Arm64Bytes.Length)
        };
        return await _jobs.CreateJobAsync(manifest, files, CancellationToken.None);
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        await using (stream)
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return ms.ToArray();
        }
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public SequenceHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request));
    }

    private sealed class MemoryFormFile : IFormFile
    {
        private readonly byte[] _bytes;
        public MemoryFormFile(byte[] bytes, string name)
        {
            _bytes = bytes;
            Name = name;
            FileName = "dshidmini.dll";
        }

        public string ContentType => "application/octet-stream";
        public string ContentDisposition => "";
        public IHeaderDictionary Headers => new HeaderDictionary();
        public long Length => _bytes.Length;
        public string Name { get; }
        public string FileName { get; }
        public void CopyTo(Stream target) => target.Write(_bytes);
        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) =>
            target.WriteAsync(_bytes, cancellationToken).AsTask();
        public Stream OpenReadStream() => new MemoryStream(_bytes, writable: false);
    }
}
