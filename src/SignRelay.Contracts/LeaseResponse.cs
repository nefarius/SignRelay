namespace SignRelay.Contracts;

public sealed class LeaseResponse
{
    public required string JobId { get; init; }
    public required JobManifestDto Manifest { get; init; }
    public required IReadOnlyList<string> UnsignedDownloadPaths { get; init; }

    /// <summary>Per-job bearer token the agent must use for all subsequent job-scoped worker calls.</summary>
    public required string LeaseToken { get; init; }

    /// <summary>UTC deadline after which the lease token is no longer valid.</summary>
    public required DateTimeOffset LeaseExpiresUtc { get; init; }
}
