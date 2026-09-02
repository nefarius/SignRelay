using SignRelay.Contracts;

namespace SignRelay.Tests;

public sealed class LeaseDownloadPathTests
{
    private const string JobId = "9694ce9d0d174c1db0a5ccde585784b1";
    private const string OtherJob = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Accepts_indexed_paths_for_the_leased_job()
    {
        var paths = new[]
        {
            ApiRoutes.WorkerUnsignedByIndex(JobId, 0),
            ApiRoutes.WorkerUnsignedByIndex(JobId, 1).TrimStart('/')
        };

        Assert.True(LeaseDownloadPath.TryValidate(JobId, paths, 2, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void Accepts_root_relative_api_paths()
    {
        var path = ApiRoutes.WorkerUnsignedByIndex(JobId, 0);
        Assert.StartsWith("/", path);
        Assert.True(LeaseDownloadPath.TryValidateOne(JobId, 0, path, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void Rejects_absolute_uri()
    {
        var paths = new[] { "https://evil.example/api/v1/worker/jobs/" + JobId + "/files/0/unsigned" };
        Assert.False(LeaseDownloadPath.TryValidate(JobId, paths, 1, out var error));
        Assert.Contains("absolute", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_traversal_and_other_job()
    {
        Assert.False(LeaseDownloadPath.TryValidateOne(JobId, 0, "api/v1/worker/jobs/" + JobId + "/files/../fail", out _));
        Assert.False(LeaseDownloadPath.TryValidateOne(JobId, 0, ApiRoutes.WorkerUnsignedByIndex(OtherJob, 0), out _));
        Assert.False(LeaseDownloadPath.TryValidateOne(JobId, 0, ApiRoutes.WorkerFail(JobId), out _));
        Assert.False(LeaseDownloadPath.TryValidateOne(JobId, 0, ApiRoutes.WorkerUnsigned(JobId, DsHidMiniSigningFixture.X64RelativePath), out _));
    }

    [Fact]
    public void Signed_paths_must_match_index_routes()
    {
        var ok = new[] { ApiRoutes.JobSignedFileByIndex(JobId, 0), ApiRoutes.JobSignedFileByIndex(JobId, 1) };
        Assert.True(LeaseDownloadPath.TryValidateSigned(JobId, ok, 2, out _));
        Assert.False(LeaseDownloadPath.TryValidateSigned(JobId, [ApiRoutes.JobSignedFile(JobId, "a.dll")], 1, out _));
    }
}
