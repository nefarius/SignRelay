namespace SignRelay.Agent;

/// <summary>Builds signtool / wdkwhere argv and resolves the executable (shared by in-process and interactive signing).</summary>
public static class SignToolCommandBuilder
{
    /// <summary>
    /// Flags that the remote manifest is NOT allowed to supply. These control certificate selection,
    /// credential material, or signing identity and must only be configured on the agent side.
    /// </summary>
    private static readonly HashSet<string> DeniedFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "/f", "/p", "/csp", "/kc", "/ph", "/ac", "/r", "/s", "/sm", "/u", "/uw"
    };

    public static List<string> BuildSignArguments(
        string filePath,
        string? thumbprint,
        string? timestampUrl,
        string[]? extraArgs)
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

        if (extraArgs is { Length: > 0 })
        {
            foreach (var arg in extraArgs)
            {
                if (string.IsNullOrWhiteSpace(arg))
                    continue;

                // Enforce the allowlist: reject flag tokens (starting with / or -)
                // that are in the denied set
                var flagPart = arg.Contains(':', StringComparison.Ordinal)
                    ? arg[..arg.IndexOf(':', StringComparison.Ordinal)]
                    : arg;
                if (DeniedFlags.Contains(flagPart))
                {
                    // Silently skip disallowed flags — operator config on agent side handles these
                    continue;
                }

                signArgs.Add(arg);
            }
        }

        signArgs.Add(filePath);
        return signArgs;
    }

    /// <summary>Resolves executable and final argv (either direct signtool or wdkwhere run signtool …).</summary>
    public static bool TryResolveCommand(string signToolPath, List<string> signArgs, out string executable, out List<string> argv)
    {
        if (TryResolveDirectSignTool(signToolPath, out var directExe))
        {
            executable = directExe;
            argv = signArgs;
            return true;
        }

        if (TryResolveWdkWhere(out var wdkExe, out var wdkNeedsCmd))
        {
            if (wdkNeedsCmd)
            {
                executable = "cmd.exe";
                argv = ["/c", wdkExe, "run", "signtool"];
                argv.AddRange(signArgs);
            }
            else
            {
                executable = wdkExe;
                argv = new List<string> { "run", "signtool" };
                argv.AddRange(signArgs);
            }
            return true;
        }

        executable = "";
        argv = signArgs;
        return false;
    }

    /// <summary>Resolves an existing signtool.exe from explicit path or PATH.</summary>
    public static bool TryResolveDirectSignTool(string? configuredPath, out string path)
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

    /// <summary>Resolves wdkwhere shim (see https://github.com/nefarius/wdkwhere). Returns whether cmd.exe /c is required.</summary>
    public static bool TryResolveWdkWhere(out string path, out bool requiresCmd)
    {
        // Prefer .exe — CreateProcess can run it directly
        var exe = FindOnPath("wdkwhere.exe");
        if (exe is not null)
        {
            path = exe;
            requiresCmd = false;
            return true;
        }

        // .cmd batch files require cmd.exe /c to execute
        var cmd = FindOnPath("wdkwhere.cmd");
        if (cmd is not null)
        {
            path = cmd;
            requiresCmd = true;
            return true;
        }

        path = "";
        requiresCmd = false;
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
