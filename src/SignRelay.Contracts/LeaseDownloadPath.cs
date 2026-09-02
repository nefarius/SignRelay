namespace SignRelay.Contracts;

/// <summary>
/// Validates server-supplied download paths before a client attaches a bearer token.
/// Only indexed routes for the specific job are accepted.
/// </summary>
public static class LeaseDownloadPath
{
    public static bool TryValidate(string jobId, IReadOnlyList<string>? paths, int expectedCount, out string? error)
    {
        if (!JobIdFormat.IsValid(jobId))
        {
            error = "Lease job id is invalid.";
            return false;
        }

        if (paths is null || paths.Count != expectedCount)
        {
            error = $"Lease download path count ({paths?.Count ?? 0}) does not match manifest file count ({expectedCount}).";
            return false;
        }

        for (var i = 0; i < paths.Count; i++)
        {
            if (!TryValidateOne(jobId, i, paths[i], out error))
                return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidateOne(string jobId, int index, string? path, out string? error)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Lease download path list contains a null or empty entry.";
            return false;
        }

        if (Uri.TryCreate(path, UriKind.Absolute, out _))
        {
            error = $"Lease download path '{path}' is an absolute URI; only relative paths are accepted.";
            return false;
        }

        if (path.Contains("..", StringComparison.Ordinal) || path.IndexOf('\\') >= 0)
        {
            error = $"Lease download path '{path}' contains a traversal or backslash segment.";
            return false;
        }

        var expected = ApiRoutes.WorkerUnsignedByIndex(jobId, index);
        var normalized = path.StartsWith('/') ? path : "/" + path;
        if (!string.Equals(normalized, expected, StringComparison.Ordinal))
        {
            error = $"Lease download path '{path}' is not the indexed unsigned URL for this job (expected '{expected}').";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidateSigned(string jobId, IReadOnlyList<string>? paths, int expectedCount, out string? error)
    {
        if (!JobIdFormat.IsValid(jobId))
        {
            error = "Job id is invalid.";
            return false;
        }

        if (paths is null || paths.Count != expectedCount)
        {
            error = $"Signed download path count ({paths?.Count ?? 0}) does not match file count ({expectedCount}).";
            return false;
        }

        for (var i = 0; i < paths.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(paths[i]) || Uri.TryCreate(paths[i], UriKind.Absolute, out _))
            {
                error = $"Signed download path '{paths[i]}' is missing or absolute.";
                return false;
            }

            var expected = ApiRoutes.JobSignedFileByIndex(jobId, i);
            var normalized = paths[i].StartsWith('/') ? paths[i] : "/" + paths[i];
            if (!string.Equals(normalized, expected, StringComparison.Ordinal))
            {
                error = $"Signed download path '{paths[i]}' is not the indexed signed URL for this job (expected '{expected}').";
                return false;
            }
        }

        error = null;
        return true;
    }
}
