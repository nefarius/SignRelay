namespace SignRelay.Contracts;

public sealed class JobEventPayload
{
    public required string Type { get; init; }
    public required JobStatus Status { get; init; }
    public string? Error { get; init; }
}
