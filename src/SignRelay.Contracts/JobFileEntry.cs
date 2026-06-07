namespace SignRelay.Contracts;

public sealed class JobFileEntry
{
    public required string RelativePath { get; init; }

    /// <summary>
    /// Optional additional signtool arguments for this specific file. Each element is a discrete argv token
    /// (no shell quoting needed). The agent enforces an allowlist; sensitive flags such as /f, /p, /csp
    /// are ignored even if supplied here — those must be configured on the agent side.
    /// </summary>
    public string[]? SignToolExtraArgs { get; init; }
}
