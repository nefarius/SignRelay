using CliWrap;
using CliWrap.Buffered;

namespace SignRelay.Agent;

public sealed class SignToolRunner
{
    private readonly ILogger<SignToolRunner> _log;

    public SignToolRunner(ILogger<SignToolRunner> log) => _log = log;

    public async Task<int> SignAsync(string signToolPath, string filePath, string? thumbprint, string? timestampUrl, string? extraArgs, CancellationToken ct)
    {
        var signArgs = new List<string> { "sign", "/v", "/fd", "sha256" };

        if (!string.IsNullOrWhiteSpace(thumbprint))
        {
            signArgs.Add("/sha1");
            signArgs.Add(thumbprint);
        }

        if (!string.IsNullOrWhiteSpace(timestampUrl))
        {
            signArgs.Add("/tr");
            signArgs.Add(timestampUrl);
            signArgs.Add("/td");
            signArgs.Add("sha256");
        }

        if (!string.IsNullOrWhiteSpace(extraArgs))
            signArgs.AddRange(extraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        signArgs.Add(filePath);

        if (TryResolveDirectSignTool(signToolPath, out var directExe))
        {
            return await RunAsync(directExe, signArgs, ct).ConfigureAwait(false);
        }

        if (TryResolveWdkWhere(out var wdkExe))
        {
            _log.LogInformation(
                "signtool.exe was not found at SignToolPath or on PATH; using {Wdk} run signtool (install from https://www.nuget.org/packages/Nefarius.Tools.WDKWhere or set SignRelayAgent__SignToolPath).",
                wdkExe);
            var wdkArgs = new List<string> { "run", "signtool" };
            wdkArgs.AddRange(signArgs);
            return await RunAsync(wdkExe, wdkArgs, ct).ConfigureAwait(false);
        }

        _log.LogError(
            "Could not find signtool.exe (configured path or PATH), and wdkwhere was not found on PATH. " +
            "Install the Windows SDK (signtool), set SignRelayAgent__SignToolPath, or install the global tool: dotnet tool install --global Nefarius.Tools.WDKWhere (see https://github.com/nefarius/wdkwhere).");
        return 1;
    }

    private async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var result = await Cli.Wrap(executable)
            .WithArguments(arguments)
            .ExecuteBufferedAsync(ct)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
            _log.LogInformation("{Out}", result.StandardOutput.Trim());
        if (!string.IsNullOrWhiteSpace(result.StandardError))
            _log.LogWarning("{Err}", result.StandardError.Trim());

        return result.ExitCode;
    }

    /// <summary>Resolves an existing signtool.exe from explicit path or PATH.</summary>
    private static bool TryResolveDirectSignTool(string? configuredPath, out string path)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var trimmed = configuredPath.Trim();
            if (File.Exists(trimmed))
            {
                path = Path.GetFullPath(trimmed);
                return true;
            }
        }

        var onPath = FindOnPath("signtool.exe");
        if (onPath is not null)
        {
            path = onPath;
            return true;
        }

        path = "";
        return false;
    }

    /// <summary>Resolves wdkwhere shim (see https://github.com/nefarius/wdkwhere).</summary>
    private static bool TryResolveWdkWhere(out string path)
    {
        foreach (var name in new[] { "wdkwhere.exe", "wdkwhere.cmd", "wdkwhere" })
        {
            var found = FindOnPath(name);
            if (found is not null)
            {
                path = found;
                return true;
            }
        }

        path = "";
        return false;
    }

    private static string? FindOnPath(string fileName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
            return null;

        foreach (var raw in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = raw.Trim().Trim('"');
            if (string.IsNullOrEmpty(dir))
                continue;

            try
            {
                var full = Path.Combine(dir, fileName);
                if (File.Exists(full))
                    return Path.GetFullPath(full);
            }
            catch (ArgumentException)
            {
                // skip invalid path segments
            }
        }

        return null;
    }
}
