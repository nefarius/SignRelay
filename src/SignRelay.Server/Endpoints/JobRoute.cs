using SignRelay.Contracts;

namespace SignRelay.Server.Api;

internal static class JobRoute
{
    public static bool TryBind(string? raw, out string jobId)
    {
        jobId = raw ?? "";
        return JobIdFormat.IsValid(jobId);
    }
}
