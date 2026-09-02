using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace SignRelay.Agent;

/// <summary>
/// Same-session helper that starts the real signing process, then hides only its
/// console HWND. Must run on the interactive desktop: Session 0 cannot call
/// <c>ShowWindow</c> on a Session 1 console.
/// </summary>
/// <remarks>
/// Do not put <c>SW_HIDE</c> on the target's <c>STARTUPINFO</c>. CSP/PIN dialogs
/// often call <c>ShowWindow(SW_SHOWDEFAULT)</c>, which then inherits that hide
/// state and fails with <c>0x800704c7</c> / <c>ERROR_CANCELLED</c>.
/// </remarks>
internal static class InteractiveConsoleExec
{
    internal const string Verb = "--hide-console-and-exec";

    internal static bool IsVerb(string[] args) =>
        args.Length > 0 && string.Equals(args[0], Verb, StringComparison.Ordinal);

    internal static List<string> BuildArguments(string targetExecutable, IReadOnlyList<string> targetArguments)
    {
        var args = new List<string>(2 + 1 + targetArguments.Count) { Verb, "--", targetExecutable };
        args.AddRange(targetArguments);
        return args;
    }

    internal static bool TryParse(string[] args, out string executable, out IReadOnlyList<string> arguments)
    {
        executable = "";
        arguments = [];
        if (!IsVerb(args))
            return false;

        var dash = Array.IndexOf(args, "--");
        if (dash < 1 || dash + 1 >= args.Length)
            return false;

        executable = args[dash + 1];
        arguments = args[(dash + 2)..];
        return executable.Length > 0;
    }

    /// <summary>
    /// Resolves how to re-enter this process as the interactive helper.
    /// Returns <c>false</c> when the host image cannot be determined.
    /// </summary>
    internal static bool TryResolveHostLaunch(out string executable, out IReadOnlyList<string> prefixArgs)
    {
        executable = "";
        prefixArgs = [];

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
            return false;

        var fileName = Path.GetFileName(processPath);
        if (string.Equals(fileName, "dotnet.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assembly = typeof(InteractiveConsoleExec).Assembly.Location;
            if (string.IsNullOrWhiteSpace(assembly))
                return false;

            executable = processPath;
            prefixArgs = ["exec", assembly];
            return true;
        }

        executable = processPath;
        prefixArgs = [];
        return true;
    }

    [SupportedOSPlatform("windows")]
    internal static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows())
            return 2;

        if (!TryParse(args, out var executable, out var arguments))
            return 1;

        try
        {
            return RunTarget(executable, arguments);
        }
        catch (Win32Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static int RunTarget(string executable, IReadOnlyList<string> arguments)
    {
        var si = new STARTUPINFOW
        {
            cb = Marshal.SizeOf<STARTUPINFOW>(),
            dwFlags = (int)STARTF_USESTDHANDLES,
            hStdInput = GetStdHandle(STD_INPUT_HANDLE),
            hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE),
            hStdError = GetStdHandle(STD_ERROR_HANDLE),
        };

        var cmdLine = new StringBuilder(InteractiveUserProcessLauncher.BuildCommandLine(executable, arguments), 32768);
        var applicationName = Path.IsPathRooted(executable) ? executable : null;
        if (!CreateProcessW(
                applicationName,
                cmdLine,
                IntPtr.Zero,
                IntPtr.Zero,
                bInheritHandles: true,
                CREATE_NEW_CONSOLE | CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT,
                IntPtr.Zero,
                null,
                ref si,
                out var pi))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var job = IntPtr.Zero;
        try
        {
            job = CreateKillOnCloseJob();
            if (job != IntPtr.Zero)
                AssignProcessToJobObject(job, pi.hProcess);

            TryHideConsoleWindow((uint)pi.dwProcessId);
            ResumeThread(pi.hThread);
            CloseHandle(pi.hThread);
            pi.hThread = IntPtr.Zero;

            TryHideConsoleWindow((uint)pi.dwProcessId, retries: 40, delayMs: 10);

            if (WaitForSingleObject(pi.hProcess, INFINITE) == WAIT_FAILED)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            if (!GetExitCodeProcess(pi.hProcess, out var exitCode))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            return (int)exitCode;
        }
        finally
        {
            if (pi.hThread != IntPtr.Zero)
                CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            if (job != IntPtr.Zero)
                CloseHandle(job);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void TryHideConsoleWindow(uint processId, int retries = 1, int delayMs = 0)
    {
        for (var i = 0; i < retries; i++)
        {
            if (i > 0)
                Thread.Sleep(delayMs);

            // CREATE_NO_WINDOW gives this helper a windowless console; detach before attaching.
            FreeConsole();
            if (!AttachConsole(processId))
                continue;

            try
            {
                var hwnd = GetConsoleWindow();
                if (hwnd != IntPtr.Zero)
                    ShowWindow(hwnd, SW_HIDE);
            }
            finally
            {
                FreeConsole();
            }

            return;
        }
    }

    [SupportedOSPlatform("windows")]
    private static IntPtr CreateKillOnCloseJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            return IntPtr.Zero;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };

        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size))
            {
                CloseHandle(job);
                return IntPtr.Zero;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        return job;
    }

    private const uint STARTF_USESTDHANDLES = 0x00000100;
    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint CREATE_NEW_CONSOLE = 0x00000010;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;
    private const int SW_HIDE = 0;
    private const int STD_INPUT_HANDLE = -10;
    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_ERROR_HANDLE = -12;
    private const uint INFINITE = 0xFFFFFFFF;
    private const uint WAIT_FAILED = 0xFFFFFFFF;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        int jobObjectInformationClass,
        IntPtr lpJobObjectInformation,
        uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
}
