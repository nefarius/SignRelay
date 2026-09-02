using SignRelay.Contracts;

namespace SignRelay.Tests;

public sealed class JobIdFormatTests
{
    [Theory]
    [InlineData("9694ce9d0d174c1db0a5ccde585784b1")]
    [InlineData("ABCDEF0123456789abcdef0123456789")]
    public void Accepts_32_hex_chars(string id) => Assert.True(JobIdFormat.IsValid(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-job-id")]
    [InlineData("../jobs/9694ce9d0d174c1db0a5ccde585784b1")]
    [InlineData("9694ce9d0d174c1db0a5ccde585784b")]
    [InlineData("9694ce9d0d174c1db0a5ccde585784b1ff")]
    [InlineData("9694ce9d0d174c1db0a5ccde585784bg")]
    public void Rejects_malformed_or_traversal_ids(string? id) => Assert.False(JobIdFormat.IsValid(id));
}
