using SignRelay.Contracts;

namespace SignRelay.Server.Data;

public sealed class JobEntity
{
    public string Id { get; set; } = "";
    public JobStatus Status { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public string JobTokenHash { get; set; } = "";
    public string ManifestJson { get; set; } = "";
    public long TotalUnsignedBytes { get; set; }

    // Lease tracking
    public string? LeaseAgentId { get; set; }
    public DateTimeOffset? LeasedUtc { get; set; }
    public string? LeaseTokenHash { get; set; }
    public DateTimeOffset? LeaseExpiresUtc { get; set; }

    /// <summary>How many times this job has been leased. Used to cap requeue attempts.</summary>
    public int LeaseAttempts { get; set; }

    public string? ErrorMessage { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
}
