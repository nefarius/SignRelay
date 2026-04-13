namespace SignRelay.Contracts;

public sealed class JobManifestDto
{
    public required List<JobFileEntry> Files { get; init; }
}
