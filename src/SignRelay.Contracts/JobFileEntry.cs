namespace SignRelay.Contracts;

public sealed class JobFileEntry
{
    public required string RelativePath { get; init; }
    public string? SignToolExtraArgs { get; init; }
}
