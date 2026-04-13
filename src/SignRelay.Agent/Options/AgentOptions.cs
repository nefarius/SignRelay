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
