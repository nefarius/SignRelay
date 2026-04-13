using SignRelay.Agent.Options;

namespace SignRelay.Agent;

public interface IJobStaging
{
    /// <summary>Absolute path to the directory for this job (created by caller or here as needed).</summary>
    string GetJobDirectory(string jobId, AgentOptions opt);

    /// <summary>
    /// When using interactive signing, grants the active console user Modify access to the job directory (Windows only).
    /// </summary>
    void EnsureInteractiveUserCanAccessJobDirectory(string jobDirectory, AgentOptions opt);
}
