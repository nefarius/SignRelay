namespace SignRelay.Contracts;

/// <summary>
/// Shared path validation used by both server and agent. Wire paths must be relative, forward-slash
/// separated, and must not contain traversal segments.
/// </summary>
public static class PathSafety
{
    // Union of OS-invalid filename chars and cross-platform denials (colon is a drive separator on
    // Windows; not in GetInvalidFileNameChars on Linux, but still unsafe in portable paths).
    private static readonly char[] InvalidSegmentChars =
        Path.GetInvalidFileNameChars().Union([':']).ToArray();

    /// <summary>
    /// Normalises <paramref name="relativePath"/> to the platform's <see cref="Path.DirectorySeparatorChar"/>
    /// separated form. Throws <see cref="InvalidOperationException"/> when the path is empty, contains
    /// traversal segments (<c>..</c> / <c>.</c>), or contains OS-invalid or cross-platform-unsafe filename characters.
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
            if (p.IndexOfAny(InvalidSegmentChars) >= 0)
                throw new InvalidOperationException($"Invalid path segment '{p}': contains disallowed characters.");
            if (Path.GetFileName(p) != p)
                throw new InvalidOperationException($"Invalid path segment '{p}'.");
        }

        return Path.Combine(parts);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="fullPath"/> is strictly under <paramref name="root"/>
    /// (including the directory separator boundary, preventing prefix-collision attacks).
    /// Uses case-insensitive comparison on Windows and case-sensitive comparison elsewhere to match
    /// the underlying filesystem semantics.
    /// </summary>
    public static bool IsUnderRoot(string fullPath, string root)
    {
        var rootWithSep = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return fullPath.StartsWith(rootWithSep, comparison);
    }

    /// <summary>
    /// Builds a wire-safe relative path for <paramref name="fullPath"/> relative to <paramref name="root"/>.
    /// Files under <paramref name="root"/> keep their relative path; files outside it (including
    /// cross-drive absolute paths on Windows) use the file name only.
    /// </summary>
    public static string ToWireRelativePath(string root, string fullPath)
    {
        if (IsUnderRoot(fullPath, root))
            return NormalizeRelativePath(Path.GetRelativePath(root, fullPath));

        var name = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(name))
            throw new InvalidOperationException("Path must include a file name.");

        return NormalizeRelativePath(name);
    }
}
