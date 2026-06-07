namespace SignRelay.Contracts;

public static class ApiRoutes
{
    public const string Prefix = "/api/v1";

    // CI endpoints
    public const string Jobs = $"{Prefix}/jobs";
    public static string JobEvents(string jobId) => $"{Prefix}/jobs/{jobId}/events";
    public static string JobSignedFile(string jobId, string fileName) => $"{Prefix}/jobs/{jobId}/signed/{Uri.EscapeDataString(fileName)}";

    // Worker endpoints
    public const string WorkerLease = $"{Prefix}/worker/lease";
    public static string WorkerUnsigned(string jobId, string fileName) => $"{Prefix}/worker/jobs/{jobId}/unsigned/{Uri.EscapeDataString(fileName)}";
    public static string WorkerSigned(string jobId) => $"{Prefix}/worker/jobs/{jobId}/signed";
    public static string WorkerComplete(string jobId) => $"{Prefix}/worker/jobs/{jobId}/complete";
    public static string WorkerFail(string jobId) => $"{Prefix}/worker/jobs/{jobId}/fail";
    public static string WorkerHeartbeat(string jobId) => $"{Prefix}/worker/jobs/{jobId}/heartbeat";
}
