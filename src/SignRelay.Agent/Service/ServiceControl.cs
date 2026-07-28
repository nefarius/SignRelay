using System.Diagnostics;
using System.Runtime.Versioning;
using CliWrap;
using CliWrap.Buffered;

namespace SignRelay.Agent.Service;

/// <summary>Thin wrapper around <c>sc.exe</c> for Windows Service registration.</summary>
[SupportedOSPlatform("windows")]
internal static class ServiceControl
{
    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunScAsync(
        IEnumerable<string> arguments,
        CancellationToken ct = default)
    {
        var result = await Cli.Wrap("sc.exe")
            .WithArguments(arguments)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct)
            .ConfigureAwait(false);

        return (result.ExitCode, result.StandardOutput, result.StandardError);
    }

    public static async Task<bool> ExistsAsync(string serviceName, CancellationToken ct = default)
    {
        var (exit, _, _) = await RunScAsync(["query", serviceName], ct).ConfigureAwait(false);
        return exit == 0;
    }

    public static async Task CreateAsync(
        string serviceName,
        string binPath,
        string displayName,
        CancellationToken ct = default)
    {
        // sc.exe requires a space after '=': key= value
        var (exit, stdout, stderr) = await RunScAsync(
            [
                "create", serviceName,
                "binPath=", binPath,
                "start=", "delayed-auto",
                "obj=", "LocalSystem",
                "DisplayName=", displayName
            ],
            ct).ConfigureAwait(false);

        if (exit != 0)
            throw new InvalidOperationException($"sc create failed (exit {exit}): {Combine(stdout, stderr)}");
    }

    public static async Task SetDescriptionAsync(string serviceName, string description, CancellationToken ct = default)
    {
        var (exit, stdout, stderr) = await RunScAsync(
            ["description", serviceName, description],
            ct).ConfigureAwait(false);

        if (exit != 0)
            throw new InvalidOperationException($"sc description failed (exit {exit}): {Combine(stdout, stderr)}");
    }

    public static async Task ConfigureFailureActionsAsync(string serviceName, CancellationToken ct = default)
    {
        var (exit, stdout, stderr) = await RunScAsync(
            [
                "failure", serviceName,
                "reset=", "86400",
                "actions=", "restart/60000/restart/60000/restart/60000"
            ],
            ct).ConfigureAwait(false);

        if (exit != 0)
            throw new InvalidOperationException($"sc failure failed (exit {exit}): {Combine(stdout, stderr)}");
    }

    public static async Task StartAsync(string serviceName, CancellationToken ct = default)
    {
        var (exit, stdout, stderr) = await RunScAsync(["start", serviceName], ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"sc start failed (exit {exit}): {Combine(stdout, stderr)}");
    }

    public static async Task StopAsync(string serviceName, CancellationToken ct = default)
    {
        var (exit, _, _) = await RunScAsync(["stop", serviceName], ct).ConfigureAwait(false);
        // 1062 = service not started — treat as success for uninstall
        if (exit is not (0 or 1062))
        {
            // Query exit again via stderr text; some sc builds return non-zero with "not started"
            // Best-effort: ignore stop failure when service is already stopped.
            _ = exit;
        }
    }

    public static async Task DeleteAsync(string serviceName, CancellationToken ct = default)
    {
        var (exit, stdout, stderr) = await RunScAsync(["delete", serviceName], ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"sc delete failed (exit {exit}): {Combine(stdout, stderr)}");
    }

    public static async Task<string> QueryStateAsync(string serviceName, CancellationToken ct = default)
    {
        var (exit, stdout, stderr) = await RunScAsync(["query", serviceName], ct).ConfigureAwait(false);
        if (exit != 0)
            return $"not installed ({Combine(stdout, stderr).Trim()})";

        foreach (var line in stdout.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("STATE", StringComparison.OrdinalIgnoreCase))
                return trimmed;
        }

        return stdout.Trim();
    }

    public static void EnsureEventLogSource(string sourceName)
    {
        try
        {
            if (!EventLog.SourceExists(sourceName))
                EventLog.CreateEventSource(sourceName, "Application");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not register Event Log source '{sourceName}'. Run as Administrator. {ex.Message}", ex);
        }
    }

    private static string Combine(string stdout, string stderr) =>
        string.Join(" ", new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
}
