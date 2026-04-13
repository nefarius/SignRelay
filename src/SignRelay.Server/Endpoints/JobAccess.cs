using System.Security.Claims;
using SignRelay.Contracts;

namespace SignRelay.Server.Api;

public static class JobAccess
{
    public static bool CanAccessJob(ClaimsPrincipal user, string jobId)
    {
        var role = user.FindFirstValue(SignRelayClaimTypes.Role);
        if (role == SignRelayClaimTypes.Ci)
            return true;
        if (role == SignRelayClaimTypes.Job && user.FindFirstValue(ClaimTypes.NameIdentifier) == jobId)
            return true;

        return false;
    }
}
