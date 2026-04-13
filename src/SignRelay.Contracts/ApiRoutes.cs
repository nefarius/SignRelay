namespace SignRelay.Contracts;

public static class ApiRoutes
{
    public const string Prefix = "/api/v1";

    public const string Jobs = $"{Prefix}/jobs";
    public static string JobEvents(string jobId) => $"{Prefix}/jobs/{jobId}/events";
    public static string JobSignedFile(string jobId, string fileName) => $"{Prefix}/jobs/{jobId}/signed/{Uri.EscapeDataString(fileName)}";

    public const string WorkerLease = $"{Prefix}/worker/lease";
    public static string WorkerUnsigned(string jobId, string fileName) => $"{Prefix}/worker/jobs/{jobId}/unsigned/{Uri.EscapeDataString(fileName)}";
    public static string WorkerSigned(string jobId) => $"{Prefix}/worker/jobs/{jobId}/signed";
    public static string WorkerComplete(string jobId) => $"{Prefix}/worker/jobs/{jobId}/complete";
}
