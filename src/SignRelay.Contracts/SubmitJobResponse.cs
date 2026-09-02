namespace SignRelay.Contracts;

public sealed class SubmitJobResponse
{
    public required string JobId { get; init; }
    public required string JobToken { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }

    /// <summary>
    /// Indexed signed-file download paths. Absent on older servers; clients fall back to
    /// <see cref="ApiRoutes.JobSignedFile"/> (basename-only paths only).
    /// </summary>
    public IReadOnlyList<string>? SignedDownloadPaths { get; init; }
}
