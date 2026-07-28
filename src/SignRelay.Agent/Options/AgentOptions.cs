namespace SignRelay.Agent.Options;

public sealed class AgentOptions
{
    public const string SectionName = "SignRelayAgent";

    public string RelayUrl { get; set; } = "http://localhost:8080";
    public string AgentToken { get; set; } = "";
    public string? AgentId { get; set; }
    /// <summary>Optional full path to <c>signtool.exe</c>. When empty, PATH then wdkwhere are tried.</summary>
    public string SignToolPath { get; set; } = "";
    public string? CertificateThumbprint { get; set; }
    public string? TimestampServerUrl { get; set; } = "http://timestamp.digicert.com";
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Maximum time the agent will wait for a single job's network operations (download, upload,
    /// complete, fail, heartbeat) before the HttpClient itself times out. Should match or exceed the
    /// server's LeaseDuration. Default: 30 minutes.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How signtool is executed (see <see cref="SigningExecutionMode"/>).</summary>
    public SigningExecutionMode SigningExecution { get; set; } = SigningExecutionMode.Auto;

    /// <summary>
    /// Root directory for job staging when <see cref="SigningExecution"/> resolves to interactive user signing.
    /// Default: <c>%ProgramData%\SignRelay\Agent\jobs</c> when unset.
    /// </summary>
    public string? JobStagingRoot { get; set; }

    /// <summary>
    /// When launching signtool in the interactive user session, load the user's profile so Current User cert store and CSPs resolve.
    /// </summary>
    public bool LoadUserProfileForInteractiveSigning { get; set; } = true;
}
