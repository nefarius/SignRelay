namespace SignRelay.Contracts;

public static class SignRelayClaimTypes
{
    public const string Role = "signrelay_role";
    public const string Ci = "ci";
    public const string Agent = "agent";
    public const string Job = "job";

    /// <summary>
    /// Per-job lease principal issued when a worker presents a valid lease token.
    /// The <see cref="System.Security.Claims.ClaimTypes.NameIdentifier"/> claim on this principal
    /// holds the job ID the lease is bound to.
    /// </summary>
    public const string Lease = "lease";
}
