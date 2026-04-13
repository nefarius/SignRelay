using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting.WindowsServices;
using SignRelay.Agent.Options;

namespace SignRelay.Agent;

internal static class SigningExecutionHelper
{
    public static bool UseInteractiveSigning(AgentOptions opt)
    {
        if (!OperatingSystem.IsWindows() || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        return opt.SigningExecution switch
        {
            SigningExecutionMode.SameProcess => false,
            SigningExecutionMode.InteractiveUser => true,
            SigningExecutionMode.Auto => WindowsServiceHelpers.IsWindowsService(),
            _ => false,
        };
    }
}
