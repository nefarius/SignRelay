using Microsoft.Extensions.Options;
using SignRelay.Agent.Options;

namespace SignRelay.Tests;

public sealed class AgentOptionsValidatorTests
{
    private readonly AgentOptionsValidator _sut = new();

    [Fact]
    public void Valid_options_succeed()
    {
        var result = _sut.Validate(null, new AgentOptions
        {
            RelayUrl = "https://relay.example.com",
            AgentToken = "a-valid-token",
            PollInterval = TimeSpan.FromSeconds(2),
            LeaseDuration = TimeSpan.FromMinutes(30),
        });

        Assert.Equal(ValidateOptionsResult.Success, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("ftp://relay.example.com")]
    [InlineData("http://relay.example.com")]
    public void Invalid_RelayUrl_fails(string url)
    {
        var result = _sut.Validate(null, new AgentOptions
        {
            RelayUrl = url,
            AgentToken = "token",
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("RelayUrl", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://localhost:8080")]
    [InlineData("http://127.0.0.1")]
    [InlineData("https://relay.example.com")]
    public void Loopback_http_or_https_RelayUrl_succeeds(string url)
    {
        var result = _sut.Validate(null, new AgentOptions
        {
            RelayUrl = url,
            AgentToken = "token",
        });

        Assert.Equal(ValidateOptionsResult.Success, result);
    }

    [Fact]
    public void Missing_AgentToken_fails()
    {
        var result = _sut.Validate(null, new AgentOptions
        {
            RelayUrl = "http://localhost:8080",
            AgentToken = "",
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("AgentToken", StringComparison.Ordinal));
    }

    [Fact]
    public void Non_positive_PollInterval_fails()
    {
        var result = _sut.Validate(null, new AgentOptions
        {
            RelayUrl = "http://localhost:8080",
            AgentToken = "token",
            PollInterval = TimeSpan.Zero,
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("PollInterval", StringComparison.Ordinal));
    }

    [Fact]
    public void Relative_JobStagingRoot_fails()
    {
        var result = _sut.Validate(null, new AgentOptions
        {
            RelayUrl = "http://localhost:8080",
            AgentToken = "token",
            JobStagingRoot = "relative\\path",
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("JobStagingRoot", StringComparison.Ordinal));
    }

    [Fact]
    public void Absolute_JobStagingRoot_succeeds()
    {
        var result = _sut.Validate(null, new AgentOptions
        {
            RelayUrl = "http://localhost:8080",
            AgentToken = "token",
            JobStagingRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "signrelay-test")),
        });

        Assert.Equal(ValidateOptionsResult.Success, result);
    }
}
