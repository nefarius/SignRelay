namespace SignRelay.Server.Options;

public sealed class SignRelayOptions
{
    public const string SectionName = "SignRelay";

    public string CiToken { get; set; } = "";
    public string AgentToken { get; set; } = "";
    public string StoragePath { get; set; } = "data";
    public long MaxTotalJobBytes { get; set; } = 512L * 1024 * 1024;
    public TimeSpan JobTimeToLive { get; set; } = TimeSpan.FromHours(2);

    /// <summary>How long a lease token remains valid before the sweeper requeues or fails the job.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Maximum number of times a job may be leased before it is permanently failed.</summary>
    public int MaxLeaseAttempts { get; set; } = 3;

    /// <summary>Grace period after a job reaches a terminal state before its artifacts are deleted from disk.</summary>
    public TimeSpan ArtifactCleanupDelay { get; set; } = TimeSpan.FromHours(1);
}
