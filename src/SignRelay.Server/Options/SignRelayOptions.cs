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

    /// <summary>
    /// How long terminal job rows are retained in SQLite after <see cref="ArtifactCleanupDelay"/> has
    /// already removed on-disk artifacts. Default: 7 days. Must be greater than or equal to
    /// <see cref="ArtifactCleanupDelay"/>.
    /// </summary>
    public TimeSpan JobRecordRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Optional outbound notifications for terminal job outcomes. Disabled when empty.</summary>
    public NotificationOptions Notifications { get; set; } = new();
}

/// <summary>Optional Discord / Gotify notification settings. All values are secrets.</summary>
public sealed class NotificationOptions
{
    /// <summary>Discord webhook URL. Empty disables Discord notifications.</summary>
    public string DiscordWebhookUrl { get; set; } = "";

    /// <summary>Gotify server base URL (e.g. https://gotify.example.com). Empty disables Gotify.</summary>
    public string GotifyBaseUrl { get; set; } = "";

    /// <summary>Gotify application token. Required when <see cref="GotifyBaseUrl"/> is set.</summary>
    public string GotifyToken { get; set; } = "";

    public bool DiscordEnabled => !string.IsNullOrWhiteSpace(DiscordWebhookUrl);
    public bool GotifyEnabled => !string.IsNullOrWhiteSpace(GotifyBaseUrl);
}
