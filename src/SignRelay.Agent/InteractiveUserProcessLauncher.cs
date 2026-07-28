using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using SignRelay.Agent.Options;

namespace SignRelay.Agent;

/// <summary>Launches a process in the active console user's session (Windows only).</summary>
public sealed class InteractiveUserProcessLauncher
{
    private readonly ILogger<InteractiveUserProcessLauncher> _log;

    public InteractiveUserProcessLauncher(ILogger<InteractiveUserProcessLauncher> log) => _log = log;

    /// <summary>Grants Modify to the active console user on <paramref name="directoryPath"/> (must exist).</summary>
    [SupportedOSPlatform("windows")]
    public bool TryGrantModifyToActiveConsoleUser(string directoryPath, out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows())
        {
            error = "Not Windows.";
            return false;
        }

        using var token = OpenActiveConsoleUserToken();
        if (token is null)
        {
            error = "No interactive user token (is a user logged on at the console?).";
            return false;
        }

        using var identity = new WindowsIdentity(token.DangerousGetHandle());
        var sid = identity.User;
        if (sid is null)
        {
            error = "Could not resolve user SID.";
            return false;
        }

        try
        {
            var dir = new DirectoryInfo(directoryPath);
            var security = dir.GetAccessControl();
            security.AddAccessRule(
                new FileSystemAccessRule(
                    sid,
                    FileSystemRights.Modify,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            dir.SetAccessControl(security);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Builds the active console user's environment block and returns it as a case-insensitive
    /// dictionary of <c>KEY=VALUE</c> pairs. Returns <c>false</c> when no console user is logged on.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public bool TryGetActiveConsoleUserEnvironment(out IReadOnlyDictionary<string, string>? env)
    {
        env = null;
        if (!OperatingSystem.IsWindows())
            return false;

        using var token = OpenActiveConsoleUserToken();
        if (token is null)
            return false;

        IntPtr block = IntPtr.Zero;
        try
        {
            if (!CreateEnvironmentBlock(out block, token.DangerousGetHandle(), false))
                return false;

            env = ParseEnvironmentBlock(block);
            return true;
        }
        finally
        {
            if (block != IntPtr.Zero)
                DestroyEnvironmentBlock(block);
        }
    }

    /// <summary>Parses a double-NUL-terminated UTF-16 environment block into KEY=VALUE pairs.</summary>
    internal static IReadOnlyDictionary<string, string> ParseEnvironmentBlock(IntPtr block)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (block == IntPtr.Zero)
            return result;

        var offset = 0;
        while (true)
        {
            var entry = Marshal.PtrToStringUni(IntPtr.Add(block, offset));
            if (string.IsNullOrEmpty(entry))
                break;

            offset += (entry.Length + 1) * sizeof(char);

            var eq = entry.IndexOf('=');
            // Skip entries that start with '=' (e.g. drive current-directory vars like =C:=C:\...)
            if (eq <= 0)
                continue;

            result[entry[..eq]] = entry[(eq + 1)..];
        }

        return result;
    }

    [SupportedOSPlatform("windows")]
    public async Task<int> RunProcessAsActiveConsoleUserAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        string logDirectory,
        AgentOptions opt,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();

        using var token = OpenActiveConsoleUserToken();
        if (token is null)
        {
            _log.LogError("Cannot launch interactive process: no user token for the active console session.");
            return 1;
        }

        var outPath = Path.Combine(logDirectory, $".signrelay.{Guid.NewGuid():N}.stdout.log");
        var errPath = Path.Combine(logDirectory, $".signrelay.{Guid.NewGuid():N}.stderr.log");

        var sa = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            lpSecurityDescriptor = IntPtr.Zero,
            bInheritHandle = true,
        };

        var hOut = CreateFileW(
            outPath,
            GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            ref sa,
            CREATE_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);
        if (hOut == INVALID_HANDLE_VALUE)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        var hErr = CreateFileW(
            errPath,
            GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            ref sa,
            CREATE_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);
        if (hErr == INVALID_HANDLE_VALUE)
        {
            CloseHandleIfValid(hOut);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var hNul = CreateFileW(
            "\\\\.\\NUL",
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            ref sa,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);
        if (hNul == INVALID_HANDLE_VALUE)
        {
            CloseHandleIfValid(hOut);
            CloseHandleIfValid(hErr);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        IntPtr env = IntPtr.Zero;
        PROFILEINFO profile = default;
        var profileLoaded = false;

        try
        {
            if (!CreateEnvironmentBlock(out env, token.DangerousGetHandle(), false))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            if (opt.LoadUserProfileForInteractiveSigning)
            {
                using var identity = new WindowsIdentity(token.DangerousGetHandle());
                profile = new PROFILEINFO
                {
                    dwSize = Marshal.SizeOf<PROFILEINFO>(),
                    dwFlags = 1, // PI_NOUI
                    lpUserName = identity.Name,
                };
                if (!LoadUserProfileW(token.DangerousGetHandle(), ref profile))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                profileLoaded = profile.hProfile != IntPtr.Zero;
            }

            var si = new STARTUPINFOW
            {
                cb = Marshal.SizeOf<STARTUPINFOW>(),
                dwFlags = (int)(STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW),
                wShowWindow = SW_HIDE,
                hStdInput = hNul,
                hStdOutput = hOut,
                hStdError = hErr,
            };

            var cmdLine = new StringBuilder(BuildCommandLine(executable, arguments), 32768);

            if (!CreateProcessAsUserW(
                    token.DangerousGetHandle(),
                    executable,
                    cmdLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    bInheritHandles: true,
                    CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW,
                    env,
                    workingDirectory,
                    ref si,
                    out var pi))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                CloseHandleIfValid(pi.hThread);

                await WaitForExitAsync(pi.hProcess, ct).ConfigureAwait(false);

                if (!GetExitCodeProcess(pi.hProcess, out var exitCode))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                // Close write ends so log files can be read reliably.
                CloseHandleIfValid(hNul);
                hNul = IntPtr.Zero;
                CloseHandleIfValid(hOut);
                hOut = IntPtr.Zero;
                CloseHandleIfValid(hErr);
                hErr = IntPtr.Zero;

                await LogOutputsAsync(outPath, errPath).ConfigureAwait(false);
                return (int)exitCode;
            }
            finally
            {
                CloseHandleIfValid(pi.hProcess);
            }
        }
        finally
        {
            if (profileLoaded)
            {
                try
                {
                    UnloadUserProfile(token.DangerousGetHandle(), profile.hProfile);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "UnloadUserProfile failed.");
                }
            }

            if (env != IntPtr.Zero)
                DestroyEnvironmentBlock(env);

            CloseHandleIfValid(hNul);
            CloseHandleIfValid(hErr);
            CloseHandleIfValid(hOut);

            TryDelete(outPath);
            TryDelete(errPath);
        }
    }

    private static void CloseHandleIfValid(IntPtr h)
    {
        if (h != IntPtr.Zero && h != INVALID_HANDLE_VALUE)
            CloseHandle(h);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not delete temp file {Path}.", path);
        }
    }

    private async Task LogOutputsAsync(string outPath, string errPath)
    {
        try
        {
            if (File.Exists(outPath))
            {
                var text = await File.ReadAllTextAsync(outPath).ConfigureAwait(false);
                text = text.Trim();
                if (text.Length > 0)
                {
                    if (text.Length > 512) text = text[..512] + "…";
                    _log.LogInformation("signtool (interactive) stdout: {Out}", text);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read interactive stdout log.");
        }

        try
        {
            if (File.Exists(errPath))
            {
                var text = await File.ReadAllTextAsync(errPath).ConfigureAwait(false);
                text = text.Trim();
                if (text.Length > 0)
                {
                    if (text.Length > 512) text = text[..512] + "…";
                    _log.LogWarning("signtool (interactive) stderr: {Err}", text);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read interactive stderr log.");
        }
    }

    private static async Task WaitForExitAsync(IntPtr hProcess, CancellationToken ct)
    {
        const uint waitMs = 500;
        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                // Terminate the child process before letting the cancellation bubble up
                TerminateProcess(hProcess, exitCode: 1);
                ct.ThrowIfCancellationRequested();
            }

            var r = WaitForSingleObject(hProcess, waitMs);
            if (r == WAIT_OBJECT_0)
                return;
            if (r == WAIT_TIMEOUT)
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
                continue;
            }

            if (r == WAIT_FAILED)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            // Unexpected status (e.g. WAIT_ABANDONED) — treat as error
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"WaitForSingleObject returned unexpected status 0x{r:X8}.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static SafeAccessTokenHandle? OpenActiveConsoleUserToken()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFFu)
            return null;

        if (!WTSQueryUserToken(sessionId, out var hToken))
            return null;

        return new SafeAccessTokenHandle(hToken);
    }

    internal static string BuildCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var sb = new StringBuilder();
        AppendOneArg(sb, executable);
        foreach (var arg in arguments)
        {
            sb.Append(' ');
            AppendOneArg(sb, arg);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Quotes a single argument for CreateProcess / CommandLineToArgvW.
    /// Implements the full backslash+quote escaping rules documented in
    /// https://learn.microsoft.com/en-us/cpp/c-language/parsing-c-command-line-arguments:
    ///   - Backslashes are literal unless immediately preceding a quote.
    ///   - To include a literal quote, precede it with a backslash (and double any backslashes
    ///     that immediately precede it).
    ///   - The closing quote must have all immediately-preceding backslashes doubled.
    /// </summary>
    internal static void AppendOneArg(StringBuilder result, string argument)
    {
        if (argument.Length == 0)
        {
            result.Append("\"\"");
            return;
        }

        var needsQuotes = argument.IndexOfAny([' ', '\t', '"']) >= 0;

        if (!needsQuotes)
        {
            result.Append(argument);
            return;
        }

        result.Append('"');
        var i = 0;
        while (i < argument.Length)
        {
            // Count a run of backslashes
            var backslashCount = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                i++;
                backslashCount++;
            }

            if (i == argument.Length)
            {
                // Trailing backslashes before the closing quote: double them
                result.Append('\\', backslashCount * 2);
                break;
            }

            if (argument[i] == '"')
            {
                // Backslashes before a quote: double them, then escape the quote
                result.Append('\\', backslashCount * 2);
                result.Append('\\');
                result.Append('"');
            }
            else
            {
                // Ordinary character: emit backslashes as-is, then the character
                result.Append('\\', backslashCount);
                result.Append(argument[i]);
            }
            i++;
        }

        result.Append('"');
    }

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint CREATE_ALWAYS = 2;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint STARTF_USESTDHANDLES = 0x00000100;
    private const uint STARTF_USESHOWWINDOW = 0x00000001;
    private const ushort SW_HIDE = 0;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint WAIT_OBJECT_0 = 0;
    private const uint WAIT_TIMEOUT = 0x00000102;
    private const uint WAIT_FAILED = 0xFFFFFFFF;

    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROFILEINFO
    {
        public int dwSize;
        public int dwFlags;
        public string? lpUserName;
        public string? lpProfilePath;
        public string? lpDefaultPath;
        public string? lpServerName;
        public string? lpPolicyPath;
        public IntPtr hProfile;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        ref SECURITY_ATTRIBUTES lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, [MarshalAs(UnmanagedType.Bool)] bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("userenv.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LoadUserProfileW(IntPtr hToken, ref PROFILEINFO lpProfile);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool UnloadUserProfile(IntPtr hToken, IntPtr hProfile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUserW(
        IntPtr hToken,
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);
}
