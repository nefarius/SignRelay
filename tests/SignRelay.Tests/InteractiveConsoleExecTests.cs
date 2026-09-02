using SignRelay.Agent;

namespace SignRelay.Tests;

public sealed class InteractiveConsoleExecTests
{
    [Fact]
    public void IsVerb_recognizes_only_exact_first_arg()
    {
        Assert.True(InteractiveConsoleExec.IsVerb([InteractiveConsoleExec.Verb, "--", "signtool.exe"]));
        Assert.False(InteractiveConsoleExec.IsVerb([]));
        Assert.False(InteractiveConsoleExec.IsVerb(["install"]));
        Assert.False(InteractiveConsoleExec.IsVerb(["--hide-console-and-exec-extra"]));
    }

    [Fact]
    public void BuildArguments_prefixes_verb_and_separator()
    {
        var args = InteractiveConsoleExec.BuildArguments(@"C:\signtool.exe", ["sign", "/fd", "SHA256"]);
        Assert.Equal(
            [InteractiveConsoleExec.Verb, "--", @"C:\signtool.exe", "sign", "/fd", "SHA256"],
            args);
    }

    [Fact]
    public void TryParse_reads_target_command()
    {
        Assert.True(InteractiveConsoleExec.TryParse(
            [InteractiveConsoleExec.Verb, "--", "signtool.exe", "sign", "/n", "Contoso"],
            out var exe,
            out var argv));
        Assert.Equal("signtool.exe", exe);
        Assert.Equal(["sign", "/n", "Contoso"], argv);
    }

    [Fact]
    public void TryParse_rejects_missing_separator_or_exe()
    {
        Assert.False(InteractiveConsoleExec.TryParse(
            [InteractiveConsoleExec.Verb, "signtool.exe"],
            out _,
            out _));
        Assert.False(InteractiveConsoleExec.TryParse(
            [InteractiveConsoleExec.Verb, "--"],
            out _,
            out _));
        Assert.False(InteractiveConsoleExec.TryParse(["install"], out _, out _));
    }

    [Fact]
    public void TryResolveHostLaunch_returns_this_host()
    {
        Assert.True(InteractiveConsoleExec.TryResolveHostLaunch(out var exe, out var prefix));
        Assert.False(string.IsNullOrWhiteSpace(exe));
        Assert.True(File.Exists(exe));
        if (prefix.Count > 0)
        {
            Assert.Equal("exec", prefix[0]);
            Assert.True(File.Exists(prefix[1]));
        }
    }
}
