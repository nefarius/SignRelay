using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SignRelay.Agent;

/// <summary>
/// Builds extra directories to search for <c>signtool.exe</c> / <c>wdkwhere</c> when the process
/// PATH (LocalSystem under the service) does not include the interactive user's tools.
/// </summary>
public static class SignToolSearchPaths
{
    /// <summary>
    /// Builds an ordered list of extra search directories.
    /// When <paramref name="interactive"/> is provided and a console user is logged on,
    /// includes that user's PATH directories and <c>%USERPROFILE%\.dotnet\tools</c>.
    /// Always appends Windows SDK <c>bin\&lt;ver&gt;\x64</c> candidates (newest first).
    /// </summary>
    public static IReadOnlyList<string> Build(InteractiveUserProcessLauncher? interactive)
    {
        var dirs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (interactive is not null
            && OperatingSystem.IsWindows()
            && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            AddConsoleUserPaths(interactive, dirs, seen);
        }

        AddWindowsSdkBins(dirs, seen);
        return dirs;
    }

    [SupportedOSPlatform("windows")]
    private static void AddConsoleUserPaths(
        InteractiveUserProcessLauncher interactive,
        List<string> dirs,
        HashSet<string> seen)
    {
        if (!interactive.TryGetActiveConsoleUserEnvironment(out var env) || env is null)
            return;

        if (env.TryGetValue("PATH", out var pathVar) && !string.IsNullOrEmpty(pathVar))
        {
            foreach (var raw in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                TryAdd(dirs, seen, raw.Trim().Trim('"'));
        }

        if (env.TryGetValue("USERPROFILE", out var profile) && !string.IsNullOrWhiteSpace(profile))
            TryAdd(dirs, seen, Path.Combine(profile.Trim(), ".dotnet", "tools"));
    }

    private static void AddWindowsSdkBins(List<string> dirs, HashSet<string> seen)
    {
        foreach (var kitsRoot in WindowsKitsRoots())
        {
            var binRoot = Path.Combine(kitsRoot, "10", "bin");
            if (!Directory.Exists(binRoot))
                continue;

            string[] versions;
            try
            {
                versions = Directory.GetDirectories(binRoot);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            var ordered = versions
                .Select(d => (Dir: d, Name: Path.GetFileName(d)))
                .Where(t => Version.TryParse(t.Name, out _))
                .OrderByDescending(t => Version.Parse(t.Name!))
                .Select(t => t.Dir);

            foreach (var verDir in ordered)
                TryAdd(dirs, seen, Path.Combine(verDir, "x64"));
        }
    }

    private static IEnumerable<string> WindowsKitsRoots()
    {
        var x86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        if (!string.IsNullOrWhiteSpace(x86))
            yield return Path.Combine(x86, "Windows Kits");

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(pf))
            yield return Path.Combine(pf, "Windows Kits");
    }

    private static void TryAdd(List<string> dirs, HashSet<string> seen, string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
            return;

        try
        {
            var full = Path.GetFullPath(dir);
            if (!Directory.Exists(full))
                return;
            if (seen.Add(full))
                dirs.Add(full);
        }
        catch (ArgumentException)
        {
            // skip invalid path segments
        }
        catch (NotSupportedException)
        {
            // skip invalid path segments
        }
    }
}
