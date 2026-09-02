using SignRelay.Contracts;

namespace SignRelay.Tests;

public sealed class ApiRoutesTests
{
    private const string JobId = "9694ce9d0d174c1db0a5ccde585784b1";

    [Fact]
    public void Indexed_routes_contain_no_encoded_separators()
    {
        var unsigned = ApiRoutes.WorkerUnsignedByIndex(JobId, 0);
        var signed = ApiRoutes.JobSignedFileByIndex(JobId, 1);

        Assert.Equal($"/api/v1/worker/jobs/{JobId}/files/0/unsigned", unsigned);
        Assert.Equal($"/api/v1/jobs/{JobId}/files/1/signed", signed);
        Assert.DoesNotContain("%2F", unsigned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%5C", unsigned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%2F", signed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%5C", signed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Legacy_nested_path_encodes_forward_slash()
    {
        var legacy = ApiRoutes.WorkerUnsigned(JobId, DsHidMiniSigningFixture.X64RelativePath);
        Assert.Contains("%2F", legacy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(JobId, legacy);
        Assert.DoesNotContain("/bin/", legacy, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_signed_nested_path_encodes_forward_slash()
    {
        var legacy = ApiRoutes.JobSignedFile(JobId, DsHidMiniSigningFixture.Arm64RelativePath);
        Assert.Contains("%2F", legacy, StringComparison.OrdinalIgnoreCase);
    }
}
