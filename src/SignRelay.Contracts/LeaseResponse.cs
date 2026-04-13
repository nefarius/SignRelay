namespace SignRelay.Contracts;

public sealed class LeaseResponse
{
    public required string JobId { get; init; }
    public required JobManifestDto Manifest { get; init; }
    public required IReadOnlyList<string> UnsignedDownloadPaths { get; init; }
}
