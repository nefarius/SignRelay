namespace SignRelay.Contracts;

public static class ApiRoutes
{
    public const string Prefix = "/api/v1";

    // CI endpoints
    public const string Jobs = $"{Prefix}/jobs";
    public static string JobEvents(string jobId) => $"{Prefix}/jobs/{jobId}/events";

    /// <summary>Indexed signed-file download. Preferred; proxy-safe (no encoded slashes).</summary>
    public static string JobSignedFileByIndex(string jobId, int index) =>
        $"{Prefix}/jobs/{jobId}/files/{index}/signed";

    /// <summary>
    /// Legacy signed-file download that embeds the relative path in one route segment.
    /// Nested paths produce <c>%2F</c> and are rejected by some reverse proxies.
    /// </summary>
    public static string JobSignedFile(string jobId, string fileName) =>
        $"{Prefix}/jobs/{jobId}/signed/{Uri.EscapeDataString(fileName)}";

    // Worker endpoints
    public const string WorkerLease = $"{Prefix}/worker/lease";

    /// <summary>Indexed unsigned-file download. Preferred; proxy-safe (no encoded slashes).</summary>
    public static string WorkerUnsignedByIndex(string jobId, int index) =>
        $"{Prefix}/worker/jobs/{jobId}/files/{index}/unsigned";

    /// <summary>
    /// Legacy unsigned-file download that embeds the relative path in one route segment.
    /// Nested paths produce <c>%2F</c> and are rejected by some reverse proxies.
    /// </summary>
    public static string WorkerUnsigned(string jobId, string fileName) =>
        $"{Prefix}/worker/jobs/{jobId}/unsigned/{Uri.EscapeDataString(fileName)}";

    public static string WorkerSigned(string jobId) => $"{Prefix}/worker/jobs/{jobId}/signed";
    public static string WorkerComplete(string jobId) => $"{Prefix}/worker/jobs/{jobId}/complete";
    public static string WorkerFail(string jobId) => $"{Prefix}/worker/jobs/{jobId}/fail";
    public static string WorkerHeartbeat(string jobId) => $"{Prefix}/worker/jobs/{jobId}/heartbeat";
}
