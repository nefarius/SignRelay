using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SignRelay.Agent.Options;

namespace SignRelay.Agent;

public sealed class JobStaging : IJobStaging
{
    private readonly ILogger<JobStaging> _log;
    private readonly InteractiveUserProcessLauncher _launcher;

    public JobStaging(ILogger<JobStaging> log, InteractiveUserProcessLauncher launcher)
    {
        _log = log;
        _launcher = launcher;
    }

    public string GetJobDirectory(string jobId, AgentOptions opt)
    {
        if (SigningExecutionHelper.UseInteractiveSigning(opt))
        {
            var root = string.IsNullOrWhiteSpace(opt.JobStagingRoot)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SignRelay", "Agent", "jobs")
                : opt.JobStagingRoot.Trim();
            return Path.Combine(root, jobId);
        }

        return Path.Combine(Path.GetTempPath(), "signrelay", jobId);
    }

    public void EnsureInteractiveUserCanAccessJobDirectory(string jobDirectory, AgentOptions opt)
    {
        if (!SigningExecutionHelper.UseInteractiveSigning(opt))
            return;

        if (!OperatingSystem.IsWindows() || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        try
        {
            if (!_launcher.TryGrantModifyToActiveConsoleUser(jobDirectory, out var error))
                _log.LogWarning("Could not grant interactive user access to job dir {Path}: {Error}", jobDirectory, error);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not grant interactive user access to job dir {Path}", jobDirectory);
        }
    }
}
