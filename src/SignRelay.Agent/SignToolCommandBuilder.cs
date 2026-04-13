namespace SignRelay.Agent;

/// <summary>Builds signtool / wdkwhere argv and resolves the executable (shared by in-process and interactive signing).</summary>
public static class SignToolCommandBuilder
{
    public static List<string> BuildSignArguments(
        string filePath,
        string? thumbprint,
        string? timestampUrl,
        string? extraArgs)
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

        if (TryResolveWdkWhere(out var wdkExe))
        {
            executable = wdkExe;
            argv = new List<string> { "run", "signtool" };
            argv.AddRange(signArgs);
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

    /// <summary>Resolves wdkwhere shim (see https://github.com/nefarius/wdkwhere).</summary>
    public static bool TryResolveWdkWhere(out string path)
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
