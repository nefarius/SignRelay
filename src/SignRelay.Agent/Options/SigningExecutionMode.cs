namespace SignRelay.Agent.Options;

public enum SigningExecutionMode
{
    /// <summary>
    /// When running as a Windows Service on Windows, launch signtool in the active console user session; otherwise run in-process.
    /// </summary>
    Auto = 0,

    /// <summary>Always run signtool in the same process (CliWrap).</summary>
    SameProcess = 1,

    /// <summary>Always launch signtool in the active console user session (Windows only).</summary>
    InteractiveUser = 2,
}
