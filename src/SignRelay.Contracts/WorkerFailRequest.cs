namespace SignRelay.Contracts;

public sealed class WorkerFailRequest
{
    public required string Error { get; init; }
}
