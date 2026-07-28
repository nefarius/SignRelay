using SignRelay.Agent.Service;

namespace SignRelay.Tests;

public sealed class ServiceCommandsParseTests
{
    [Fact]
    public void IsServiceVerb_recognizes_known_verbs()
    {
        Assert.True(ServiceCommands.IsServiceVerb(["install"]));
        Assert.True(ServiceCommands.IsServiceVerb(["uninstall", "--purge"]));
        Assert.True(ServiceCommands.IsServiceVerb(["status"]));
        Assert.False(ServiceCommands.IsServiceVerb([]));
        Assert.False(ServiceCommands.IsServiceVerb(["run"]));
    }

    [Fact]
    public void ParseInstall_reads_all_flags()
    {
        var o = ServiceCommands.ParseInstall(
        [
            "--relay-url", "https://relay.example.com",
            "--token", "secret-token",
            "--agent-id", "desktop-1",
            "--thumbprint", "ABCD1234",
            "--timestamp-url", "http://ts.example.com",
            "--signtool", @"C:\Tools\signtool.exe",
            "--signing-execution", "InteractiveUser",
            "--service-name", "MyAgent",
            "--start",
        ]);

        Assert.Equal("https://relay.example.com", o.RelayUrl);
        Assert.Equal("secret-token", o.Token);
        Assert.Equal("desktop-1", o.AgentId);
        Assert.Equal("ABCD1234", o.Thumbprint);
        Assert.Equal("http://ts.example.com", o.TimestampUrl);
        Assert.Equal(@"C:\Tools\signtool.exe", o.SignTool);
        Assert.Equal("InteractiveUser", o.SigningExecution);
        Assert.Equal("MyAgent", o.ServiceName);
        Assert.True(o.Start);
        Assert.False(o.Help);
    }

    [Fact]
    public void ParseInstall_help_flag()
    {
        var o = ServiceCommands.ParseInstall(["--help"]);
        Assert.True(o.Help);
    }

    [Fact]
    public void ParseInstall_unknown_flag_throws()
    {
        Assert.Throws<ArgumentException>(() => ServiceCommands.ParseInstall(["--nope"]));
    }

    [Fact]
    public void ParseInstall_missing_value_throws()
    {
        Assert.Throws<ArgumentException>(() => ServiceCommands.ParseInstall(["--token"]));
    }

    [Fact]
    public void ParseUninstall_purge_and_service_name()
    {
        var o = ServiceCommands.ParseUninstall(["--purge", "--service-name", "X"]);
        Assert.True(o.Purge);
        Assert.Equal("X", o.ServiceName);
    }

    [Fact]
    public void ParseStatus_default_service_name()
    {
        var o = ServiceCommands.ParseStatus([]);
        Assert.Equal(AgentPaths.DefaultServiceName, o.ServiceName);
    }
}
