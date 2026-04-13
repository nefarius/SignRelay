namespace SignRelay.Server.Options;

public sealed class SignRelayOptions
{
    public const string SectionName = "SignRelay";

    public string CiToken { get; set; } = "";
    public string AgentToken { get; set; } = "";
    public string StoragePath { get; set; } = "data";
    public long MaxTotalJobBytes { get; set; } = 512L * 1024 * 1024;
    public TimeSpan JobTimeToLive { get; set; } = TimeSpan.FromHours(2);
}
