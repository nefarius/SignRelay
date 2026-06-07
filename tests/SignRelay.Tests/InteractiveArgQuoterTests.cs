using System.Text;
using SignRelay.Agent;

namespace SignRelay.Tests;

public sealed class InteractiveArgQuoterTests
{
    private static string Quote(string arg)
    {
        var sb = new StringBuilder();
        InteractiveUserProcessLauncher.AppendOneArg(sb, arg);
        return sb.ToString();
    }

    [Fact]
    public void EmptyArg_ProducesEmptyQuotes()
    {
        Assert.Equal("\"\"", Quote(""));
    }

    [Fact]
    public void SimpleArg_NoQuoting()
    {
        Assert.Equal("simple", Quote("simple"));
    }

    [Fact]
    public void ArgWithSpace_IsQuoted()
    {
        Assert.Equal("\"hello world\"", Quote("hello world"));
    }

    [Fact]
    public void ArgWithEmbeddedQuote_EscapedCorrectly()
    {
        // Input: say "hi"  → "say \"hi\""
        Assert.Equal("\"say \\\"hi\\\"\"", Quote("say \"hi\""));
    }

    [Fact]
    public void ArgWithTrailingBackslash_DoubledBeforeClosingQuote()
    {
        // A path with a space forces quoting; trailing backslash must be doubled
        // Input: C:\My Path\  → "C:\My Path\\"
        Assert.Equal("\"C:\\My Path\\\\\"", Quote("C:\\My Path\\"));
    }

    [Fact]
    public void ArgWithTrailingBackslash_NoSpaces_NotQuoted()
    {
        // No spaces → no quoting; trailing backslash is left as-is
        Assert.Equal("C:\\path\\", Quote("C:\\path\\"));
    }

    [Fact]
    public void ArgWithBackslashBeforeQuote_DoubledAndQuoteEscaped()
    {
        // Input: a\"b → "a\\\"b"
        Assert.Equal("\"a\\\\\\\"b\"", Quote("a\\\"b"));
    }

    [Fact]
    public void BuildCommandLine_RoundTripsSimpleArgs()
    {
        var result = InteractiveUserProcessLauncher.BuildCommandLine("prog.exe", ["arg1", "arg2"]);
        Assert.Equal("prog.exe arg1 arg2", result);
    }

    [Fact]
    public void BuildCommandLine_QuotesArgsWithSpaces()
    {
        var result = InteractiveUserProcessLauncher.BuildCommandLine("prog.exe", ["C:\\My Path\\file.exe"]);
        Assert.Contains("\"C:\\My Path\\file.exe\"", result);
    }
}
