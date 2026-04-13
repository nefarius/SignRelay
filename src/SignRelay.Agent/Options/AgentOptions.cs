namespace SignRelay.Agent.Options;

public sealed class AgentOptions
{
    public const string SectionName = "SignRelayAgent";

    public string RelayUrl { get; set; } = "http://localhost:8080";
    public string AgentToken { get; set; } = "";
    public string? AgentId { get; set; }
    public string SignToolPath { get; set; } = @"C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe";
    public string? CertificateThumbprint { get; set; }
    public string? TimestampServerUrl { get; set; } = "http://timestamp.digicert.com";
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
}
