using System.Runtime.InteropServices;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Options;
using SignRelay.Agent.Options;

namespace SignRelay.Agent;

public sealed class SignToolRunner
{
    private readonly IOptions<AgentOptions> _opt;
    private readonly InteractiveUserProcessLauncher _interactive;
    private readonly ILogger<SignToolRunner> _log;

    public SignToolRunner(
        IOptions<AgentOptions> opt,
        InteractiveUserProcessLauncher interactive,
        ILogger<SignToolRunner> log)
    {
        _opt = opt;
        _interactive = interactive;
        _log = log;
    }

    public async Task<int> SignAsync(string signToolPath, string filePath, string? thumbprint, string? timestampUrl, string[]? extraArgs, CancellationToken ct)
    {
        var signArgs = SignToolCommandBuilder.BuildSignArguments(filePath, thumbprint, timestampUrl, extraArgs);

        if (!SignToolCommandBuilder.TryResolveCommand(signToolPath, signArgs, out var executable, out var argv))
        {
            _log.LogError(
                "Could not find signtool.exe (configured path or PATH), and wdkwhere was not found on PATH. " +
                "Install the Windows SDK (signtool), set SignRelayAgent__SignToolPath, or install the global tool: dotnet tool install --global Nefarius.Tools.WDKWhere (see https://github.com/nefarius/wdkwhere).");
            return 1;
        }

        var opt = _opt.Value;
        if (SigningExecutionHelper.UseInteractiveSigning(opt) && OperatingSystem.IsWindows() && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var workDir = Path.GetDirectoryName(filePath);
            var logDir = workDir ?? Path.GetTempPath();
            return await _interactive.RunProcessAsActiveConsoleUserAsync(
                executable,
                argv,
                string.IsNullOrEmpty(workDir) ? null : workDir,
                logDir,
                opt,
                ct).ConfigureAwait(false);
        }

        return await RunDirectAsync(executable, argv, ct).ConfigureAwait(false);
    }

    private async Task<int> RunDirectAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var result = await Cli.Wrap(executable)
            .WithArguments(arguments)
            .ExecuteBufferedAsync(ct)
            .ConfigureAwait(false);

        // Log exit code + truncated, sanitized output. Avoid verbose stdout/stderr that may
        // contain certificate details or password-related error text.
        _log.LogInformation("signtool exited {ExitCode}.", result.ExitCode);

        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            var out2 = result.StandardOutput.Trim();
            if (out2.Length > 512)
                out2 = out2[..512] + "…";
            _log.LogInformation("signtool stdout: {Out}", out2);
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            var err = result.StandardError.Trim();
            if (err.Length > 512)
                err = err[..512] + "…";
            _log.LogWarning("signtool stderr: {Err}", err);
        }

        return result.ExitCode;
    }
}
