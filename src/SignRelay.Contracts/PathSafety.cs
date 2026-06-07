namespace SignRelay.Contracts;

/// <summary>
/// Shared path validation used by both server and agent. Wire paths must be relative, forward-slash
/// separated, and must not contain traversal segments.
/// </summary>
public static class PathSafety
{
    /// <summary>
    /// Normalises <paramref name="relativePath"/> to the platform's <see cref="Path.DirectorySeparatorChar"/>
    /// separated form. Throws <see cref="InvalidOperationException"/> when the path is empty, contains
    /// traversal segments (<c>..</c> / <c>.</c>), or contains OS-invalid filename characters.
    /// </summary>
    public static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidOperationException("Relative path must not be empty or whitespace.");

        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new InvalidOperationException("Relative path must not be empty.");

        foreach (var p in parts)
        {
            if (p is ".." or ".")
                throw new InvalidOperationException($"Invalid path segment '{p}'.");
            if (Path.GetFileName(p) != p)
                throw new InvalidOperationException($"Invalid path segment '{p}'.");
        }

        return Path.Combine(parts);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="fullPath"/> is strictly under <paramref name="root"/>
    /// (including the directory separator boundary, preventing prefix-collision attacks).
    /// </summary>
    public static bool IsUnderRoot(string fullPath, string root)
    {
        var rootWithSep = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
    }
}
