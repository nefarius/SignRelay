using SignRelay.Agent;

namespace SignRelay.Tests;

public sealed class SignToolCommandBuilderTests
{
    [Fact]
    public void BuildSignArguments_BasicArgs_ContainRequiredFlags()
    {
        var args = SignToolCommandBuilder.BuildSignArguments(
            "file.exe", "ABCD1234", null, "http://ts.example.com", null);

        Assert.Contains("sign", args);
        Assert.Contains("/sha1", args);
        Assert.Contains("ABCD1234", args);
        Assert.Contains("/tr", args);
        Assert.Contains("http://ts.example.com", args);
        Assert.Contains("file.exe", args);
        // file path must be last
        Assert.Equal("file.exe", args[^1]);
    }

    [Fact]
    public void BuildSignArguments_SubjectName_AddsNameFlag()
    {
        var args = SignToolCommandBuilder.BuildSignArguments(
            "file.exe", null, "Nefarius Software Solutions e.U.", null, null);

        Assert.Contains("/n", args);
        var idx = args.IndexOf("/n");
        Assert.Equal("Nefarius Software Solutions e.U.", args[idx + 1]);
        Assert.DoesNotContain("/sha1", args);
    }

    [Fact]
    public void BuildSignArguments_ThumbprintAndSubjectName_BothPresent()
    {
        var args = SignToolCommandBuilder.BuildSignArguments(
            "file.exe", "ABCD1234", "Nefarius Software Solutions e.U.", null, null);

        Assert.Contains("/sha1", args);
        Assert.Contains("ABCD1234", args);
        Assert.Contains("/n", args);
        Assert.Contains("Nefarius Software Solutions e.U.", args);
    }

    [Fact]
    public void BuildSignArguments_NoThumbprint_OmitsHashFlag()
    {
        var args = SignToolCommandBuilder.BuildSignArguments("file.exe", null, null, null, null);
        Assert.DoesNotContain("/sha1", args);
        Assert.DoesNotContain("/n", args);
    }

    [Fact]
    public void BuildSignArguments_NoTimestamp_OmitsTimestampFlags()
    {
        var args = SignToolCommandBuilder.BuildSignArguments("file.exe", null, null, null, null);
        Assert.DoesNotContain("/tr", args);
        Assert.DoesNotContain("/td", args);
    }

    [Fact]
    public void BuildSignArguments_ExtraArgs_PassedAsDiscreteTokens()
    {
        var args = SignToolCommandBuilder.BuildSignArguments("file.exe", null, null, null, ["/d", "My App"]);
        Assert.Contains("/d", args);
        Assert.Contains("My App", args);
        // "My App" must be its own token (not split further)
        var idx = args.IndexOf("/d");
        Assert.True(idx >= 0);
        Assert.Equal("My App", args[idx + 1]);
    }

    [Theory]
    [InlineData("/f")]
    [InlineData("/p")]
    [InlineData("/csp")]
    [InlineData("/kc")]
    [InlineData("/ph")]
    [InlineData("/sha1")]
    [InlineData("/n")]
    [InlineData("/i")]
    [InlineData("/c")]
    [InlineData("/a")]
    public void BuildSignArguments_DeniedExtraFlags_AreStripped(string deniedFlag)
    {
        var args = SignToolCommandBuilder.BuildSignArguments("file.exe", null, null, null, [deniedFlag, "value"]);
        Assert.DoesNotContain(deniedFlag, args);
        // The value token following the denied flag must also be stripped
        Assert.DoesNotContain("value", args);
    }

    [Theory]
    [InlineData("-f")]
    [InlineData("-sha1")]
    [InlineData("-n")]
    public void BuildSignArguments_DeniedFlagsWithDashPrefix_AreStripped(string deniedFlag)
    {
        var args = SignToolCommandBuilder.BuildSignArguments("file.exe", null, null, null, [deniedFlag, "value"]);
        Assert.DoesNotContain(deniedFlag, args);
        Assert.DoesNotContain("value", args);
    }

    [Fact]
    public void BuildSignArguments_EmptyExtraArgTokens_AreSkipped()
    {
        var before = SignToolCommandBuilder.BuildSignArguments("file.exe", null, null, null, null);
        var after = SignToolCommandBuilder.BuildSignArguments("file.exe", null, null, null, ["", "  "]);
        Assert.Equal(before, after);
    }
}
