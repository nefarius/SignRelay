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

    [Fact]
    public void TryResolveDirectSignTool_ExtraDirectory_TakesPrecedenceOverMissingPath()
    {
        var dir = Directory.CreateTempSubdirectory("signrelay-extra-");
        try
        {
            var tool = Path.Combine(dir.FullName, "signtool.exe");
            File.WriteAllBytes(tool, [0]);

            Assert.True(SignToolCommandBuilder.TryResolveDirectSignTool(
                configuredPath: null,
                out var path,
                extraSearchDirectories: [dir.FullName]));
            Assert.Equal(Path.GetFullPath(tool), path);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryResolveWdkWhere_ExtraDirectory_FindsExe()
    {
        var dir = Directory.CreateTempSubdirectory("signrelay-wdk-");
        try
        {
            var tool = Path.Combine(dir.FullName, "wdkwhere.exe");
            File.WriteAllBytes(tool, [0]);

            Assert.True(SignToolCommandBuilder.TryResolveWdkWhere(
                out var path,
                out var requiresCmd,
                extraSearchDirectories: [dir.FullName]));
            Assert.Equal(Path.GetFullPath(tool), path);
            Assert.False(requiresCmd);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryResolveDirectSignTool_EmptyExtraDirectory_DoesNotInventHit()
    {
        var empty = Directory.CreateTempSubdirectory("signrelay-empty-");
        try
        {
            // An empty extra dir must never satisfy resolution by itself. Process PATH may still
            // resolve signtool on developer machines — only assert the empty dir contributed nothing
            // when an explicit missing configured path is supplied and resolution somehow succeeds.
            var missing = Path.Combine(empty.FullName, "does-not-exist-signtool.exe");
            if (SignToolCommandBuilder.TryResolveDirectSignTool(
                    configuredPath: missing,
                    out var path,
                    extraSearchDirectories: [empty.FullName]))
            {
                Assert.DoesNotContain(
                    empty.FullName,
                    path,
                    StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }

    [Fact]
    public void DescribeResolution_ExtraDirectory_ReportsPathAndSource()
    {
        var dir = Directory.CreateTempSubdirectory("signrelay-desc-");
        try
        {
            var tool = Path.Combine(dir.FullName, "signtool.exe");
            File.WriteAllBytes(tool, [0]);

            var desc = SignToolCommandBuilder.DescribeResolution(
                configuredPath: null,
                extraSearchDirectories: [dir.FullName]);
            Assert.StartsWith("extra search → ", desc, StringComparison.Ordinal);
            Assert.Contains(Path.GetFullPath(tool), desc, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void DescribeResolutionSource_DotNetToolsAndSdk_LabelsCorrectly()
    {
        var toolsDir = Path.Combine(Path.GetTempPath(), "user", ".dotnet", "tools");
        var sdkDir = Path.Combine(
            Path.GetTempPath(), "Windows Kits", "10", "bin", "10.0.22621.0", "x64");
        var toolsExe = Path.Combine(toolsDir, "signtool.exe");
        var sdkExe = Path.Combine(sdkDir, "signtool.exe");

        Assert.Equal(
            ".dotnet\\tools",
            SignToolCommandBuilder.DescribeResolutionSource(toolsExe, null, [toolsDir]));
        Assert.Equal(
            "Windows SDK",
            SignToolCommandBuilder.DescribeResolutionSource(sdkExe, null, [sdkDir]));
        Assert.Equal(
            "PATH",
            SignToolCommandBuilder.DescribeResolutionSource(toolsExe, null, extraSearchDirectories: null));
    }

    [Fact]
    public void DescribeResolutionSource_ExplicitPath_Wins()
    {
        var dir = Directory.CreateTempSubdirectory("signrelay-explicit-");
        try
        {
            var tool = Path.Combine(dir.FullName, "signtool.exe");
            File.WriteAllBytes(tool, [0]);
            var full = Path.GetFullPath(tool);

            Assert.Equal(
                "explicit path",
                SignToolCommandBuilder.DescribeResolutionSource(full, tool, [dir.FullName]));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
