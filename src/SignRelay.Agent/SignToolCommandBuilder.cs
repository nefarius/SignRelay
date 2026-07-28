namespace SignRelay.Agent;

/// <summary>Builds signtool / wdkwhere argv and resolves the executable (shared by in-process and interactive signing).</summary>
public static class SignToolCommandBuilder
{
    /// <summary>
    /// Flags that the remote manifest is NOT allowed to supply. These control certificate selection,
    /// credential material, or signing identity and must only be configured on the agent side.
    /// Both '/' and '-' prefixes are normalised before lookup, so "-sha1" is treated as "/sha1".
    /// </summary>
    private static readonly HashSet<string> DeniedFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        // Credential / cert material
        "/f", "/p", "/csp", "/kc",
        // Cert selection (thumbprint, subject, issuer, cert file, automatic)
        "/sha1", "/n", "/i", "/c", "/a",
        // Trust/chain
        "/ph", "/ac", "/r",
        // Store selection
        "/s", "/sm",
        // Usage / extended key usage
        "/u", "/uw"
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
            for (var i = 0; i < extraArgs.Length; i++)
            {
                var arg = extraArgs[i];
                if (string.IsNullOrWhiteSpace(arg))
                    continue;

                // Extract the flag portion (everything before ':' if combined, else the whole token)
                var flagPart = arg.Contains(':', StringComparison.Ordinal)
                    ? arg[..arg.IndexOf(':', StringComparison.Ordinal)]
                    : arg;

                // Normalise '-' prefix to '/' so both "-sha1" and "/sha1" hit the same entry
                if (flagPart.Length > 0 && flagPart[0] == '-')
                    flagPart = "/" + flagPart[1..];

                if (DeniedFlags.Contains(flagPart))
                {
                    // Skip the flag. If the value is a separate (non-flag-looking) token, skip
                    // it too so callers can't smuggle credential material via flag+value pairs.
                    if (!arg.Contains(':', StringComparison.Ordinal)
                        && i + 1 < extraArgs.Length
                        && !string.IsNullOrWhiteSpace(extraArgs[i + 1]))
                    {
                        var next = extraArgs[i + 1].TrimStart();
                        if (!next.StartsWith('/') && !next.StartsWith('-'))
                            i++;
                    }
                    continue;
                }

                signArgs.Add(arg);
            }
        }

        signArgs.Add(filePath);
        return signArgs;
    }

    /// <summary>Describes how signtool will be resolved (for startup diagnostics).</summary>
    public static string DescribeResolution(string? configuredPath)
    {
        if (TryResolveDirectSignTool(configuredPath, out var direct))
        {
            var viaConfigured = !string.IsNullOrWhiteSpace(configuredPath)
                                && File.Exists(configuredPath.Trim());
            return viaConfigured
                ? $"explicit path → {direct}"
                : $"PATH → {direct}";
        }

        if (TryResolveWdkWhere(out var wdk, out var needsCmd))
            return needsCmd
                ? $"wdkwhere (.cmd via cmd.exe) → {wdk}"
                : $"wdkwhere → {wdk}";

        return "NOT FOUND (set SignToolPath, add signtool to PATH, or install Nefarius.Tools.WDKWhere)";
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
