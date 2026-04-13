namespace SignRelay.Contracts;

public sealed class SubmitJobResponse
{
    public required string JobId { get; init; }
    public required string JobToken { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
}
