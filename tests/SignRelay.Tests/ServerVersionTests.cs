using SignRelay.Server;

namespace SignRelay.Tests;

public sealed class ServerVersionTests
{
    [Fact]
    public void Current_is_non_empty_and_has_no_build_metadata()
    {
        Assert.False(string.IsNullOrWhiteSpace(ServerVersion.Current));
        Assert.DoesNotContain('+', ServerVersion.Current);
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3+abc123", "1.2.3")]
    [InlineData("0.0.0-alpha.0.12+deadbeef", "0.0.0-alpha.0.12")]
    public void StripBuildMetadata_removes_plus_suffix(string input, string expected)
    {
        Assert.Equal(expected, ServerVersion.StripBuildMetadata(input));
    }
}
