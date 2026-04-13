namespace SignRelay.Server.Services;

public static class PathSafety
{
    public static string NormalizeRelativePath(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            if (p is ".." or ".")
                throw new InvalidOperationException("Invalid path segment.");
            if (Path.GetFileName(p) != p)
                throw new InvalidOperationException("Invalid path segment.");
        }

        return Path.Combine(parts);
    }
}
