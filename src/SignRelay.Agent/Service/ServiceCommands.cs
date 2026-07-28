using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SignRelay.Agent.Options;

namespace SignRelay.Agent.Service;

/// <summary>
/// CLI verbs for Windows Service lifecycle: <c>install</c>, <c>uninstall</c>, <c>status</c>.
/// These run before the worker host is built.
/// </summary>
public static class ServiceCommands
{
    private static readonly HashSet<string> Verbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "install", "uninstall", "status"
    };

    public static bool IsServiceVerb(string[] args) =>
        args.Length > 0 && Verbs.Contains(args[0]);

    /// <summary>Dispatches a service verb. Returns a process exit code.</summary>
    public static Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Service install/uninstall/status is only supported on Windows.");
            return Task.FromResult(2);
        }

        return RunWindowsAsync(args);
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunWindowsAsync(string[] args)
    {
        var verb = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        return verb switch
        {
            "install" => await InstallAsync(rest).ConfigureAwait(false),
            "uninstall" => await UninstallAsync(rest).ConfigureAwait(false),
            "status" => await StatusAsync(rest).ConfigureAwait(false),
            _ => 2
        };
    }

    public sealed class InstallOptions
    {
        public string? RelayUrl { get; set; }
        public string? Token { get; set; }
        public string? AgentId { get; set; }
        public string? Thumbprint { get; set; }
        public string? SubjectName { get; set; }
        public string? TimestampUrl { get; set; }
        public string? SignTool { get; set; }
        public string? SigningExecution { get; set; }
        public string ServiceName { get; set; } = AgentPaths.DefaultServiceName;
        public bool Start { get; set; }
        public bool Help { get; set; }
    }

    public sealed class UninstallOptions
    {
        public string ServiceName { get; set; } = AgentPaths.DefaultServiceName;
        public bool Purge { get; set; }
        public bool Help { get; set; }
    }

    public sealed class StatusOptions
    {
        public string ServiceName { get; set; } = AgentPaths.DefaultServiceName;
        public bool Help { get; set; }
    }

    public static InstallOptions ParseInstall(string[] args)
    {
        var o = new InstallOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a.ToLowerInvariant())
            {
                case "-h":
                case "--help":
                    o.Help = true;
                    break;
                case "--relay-url":
                    o.RelayUrl = RequireValue(args, ref i, a);
                    break;
                case "--token":
                    o.Token = RequireValue(args, ref i, a);
                    break;
                case "--agent-id":
                    o.AgentId = RequireValue(args, ref i, a);
                    break;
                case "--thumbprint":
                    o.Thumbprint = RequireValue(args, ref i, a);
                    break;
                case "--subject-name":
                    o.SubjectName = RequireValue(args, ref i, a);
                    break;
                case "--timestamp-url":
                    o.TimestampUrl = RequireValue(args, ref i, a);
                    break;
                case "--signtool":
                    o.SignTool = RequireValue(args, ref i, a);
                    break;
                case "--signing-execution":
                    o.SigningExecution = RequireValue(args, ref i, a);
                    break;
                case "--service-name":
                    o.ServiceName = RequireValue(args, ref i, a);
                    break;
                case "--start":
                    o.Start = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown install option: {a}");
            }
        }

        return o;
    }

    public static UninstallOptions ParseUninstall(string[] args)
    {
        var o = new UninstallOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a.ToLowerInvariant())
            {
                case "-h":
                case "--help":
                    o.Help = true;
                    break;
                case "--service-name":
                    o.ServiceName = RequireValue(args, ref i, a);
                    break;
                case "--purge":
                    o.Purge = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown uninstall option: {a}");
            }
        }

        return o;
    }

    public static StatusOptions ParseStatus(string[] args)
    {
        var o = new StatusOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a.ToLowerInvariant())
            {
                case "-h":
                case "--help":
                    o.Help = true;
                    break;
                case "--service-name":
                    o.ServiceName = RequireValue(args, ref i, a);
                    break;
                default:
                    throw new ArgumentException($"Unknown status option: {a}");
            }
        }

        return o;
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> InstallAsync(string[] args)
    {
        InstallOptions opt;
        try
        {
            opt = ParseInstall(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintInstallHelp();
            return 2;
        }

        if (opt.Help)
        {
            PrintInstallHelp();
            return 0;
        }

        if (!IsElevated())
        {
            Console.Error.WriteLine("install requires an elevated (Administrator) console.");
            return 2;
        }

        opt.RelayUrl = PromptIfMissing(opt.RelayUrl, "Relay URL (HTTPS)", required: true);
        opt.Token = PromptIfMissing(opt.Token, "Agent token", required: true, secret: true);

        if (string.IsNullOrWhiteSpace(opt.RelayUrl) || string.IsNullOrWhiteSpace(opt.Token))
        {
            Console.Error.WriteLine("--relay-url and --token are required (or provide them interactively).");
            return 2;
        }

        if (!Uri.TryCreate(opt.RelayUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Console.Error.WriteLine("--relay-url must be an absolute http or https URL.");
            return 2;
        }

        if (!string.IsNullOrWhiteSpace(opt.SigningExecution)
            && !Enum.TryParse<SigningExecutionMode>(opt.SigningExecution, ignoreCase: true, out _))
        {
            Console.Error.WriteLine("--signing-execution must be Auto, SameProcess, or InteractiveUser.");
            return 2;
        }

        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not resolve the agent executable path.");

        WriteMachineSettings(opt);
        ServiceControl.EnsureEventLogSource(AgentPaths.EventLogSourceName);

        if (await ServiceControl.ExistsAsync(opt.ServiceName).ConfigureAwait(false))
        {
            Console.WriteLine($"Service '{opt.ServiceName}' already exists — updating config and repairing service settings.");
            Console.WriteLine($"Wrote machine settings: {AgentPaths.MachineSettingsFile}");
            try
            {
                await ServiceControl.SetDescriptionAsync(opt.ServiceName, "SignRelay signing agent")
                    .ConfigureAwait(false);
                await ServiceControl.ConfigureFailureActionsAsync(opt.ServiceName).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }

            if (opt.Start)
            {
                try
                {
                    await ServiceControl.StartAsync(opt.ServiceName).ConfigureAwait(false);
                    Console.WriteLine($"Started service '{opt.ServiceName}'.");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Could not start service: {ex.Message}");
                    return 1;
                }
            }

            return 0;
        }

        try
        {
            // Quote the binary path for sc.exe when it contains spaces
            var binPath = exePath.Contains(' ', StringComparison.Ordinal) ? $"\"{exePath}\"" : exePath;
            await ServiceControl.CreateAsync(opt.ServiceName, binPath, AgentPaths.DefaultServiceDisplayName)
                .ConfigureAwait(false);
            await ServiceControl.SetDescriptionAsync(opt.ServiceName, "SignRelay signing agent")
                .ConfigureAwait(false);
            await ServiceControl.ConfigureFailureActionsAsync(opt.ServiceName).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        Console.WriteLine($"Installed service '{opt.ServiceName}'.");
        Console.WriteLine($"  Binary:  {exePath}");
        Console.WriteLine($"  Config:  {AgentPaths.MachineSettingsFile}");
        Console.WriteLine($"  Logs:    {AgentPaths.LogsDirectory}");

        if (opt.Start)
        {
            try
            {
                await ServiceControl.StartAsync(opt.ServiceName).ConfigureAwait(false);
                Console.WriteLine($"Started service '{opt.ServiceName}'.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Service installed but start failed: {ex.Message}");
                return 1;
            }
        }
        else
        {
            Console.WriteLine($"Start with: sc.exe start {opt.ServiceName}");
            Console.WriteLine($"Or:        SignRelay.Agent.exe install --start ...");
        }

        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> UninstallAsync(string[] args)
    {
        UninstallOptions opt;
        try
        {
            opt = ParseUninstall(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintUninstallHelp();
            return 2;
        }

        if (opt.Help)
        {
            PrintUninstallHelp();
            return 0;
        }

        if (!IsElevated())
        {
            Console.Error.WriteLine("uninstall requires an elevated (Administrator) console.");
            return 2;
        }

        if (await ServiceControl.ExistsAsync(opt.ServiceName).ConfigureAwait(false))
        {
            try
            {
                await ServiceControl.StopAsync(opt.ServiceName).ConfigureAwait(false);
                // SCM needs a moment after stop before delete
                await Task.Delay(500).ConfigureAwait(false);
                await ServiceControl.DeleteAsync(opt.ServiceName).ConfigureAwait(false);
                Console.WriteLine($"Deleted service '{opt.ServiceName}'.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }
        else
        {
            Console.WriteLine($"Service '{opt.ServiceName}' is not installed.");
        }

        if (opt.Purge)
        {
            try
            {
                if (Directory.Exists(AgentPaths.RootDirectory))
                {
                    Directory.Delete(AgentPaths.RootDirectory, recursive: true);
                    Console.WriteLine($"Removed {AgentPaths.RootDirectory}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not purge machine data: {ex.Message}");
                return 1;
            }
        }

        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> StatusAsync(string[] args)
    {
        StatusOptions opt;
        try
        {
            opt = ParseStatus(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintStatusHelp();
            return 2;
        }

        if (opt.Help)
        {
            PrintStatusHelp();
            return 0;
        }

        if (!IsElevated())
        {
            // status is useful without elevation for query; warn but continue for config/health
            Console.WriteLine("(not elevated — Event Log / ACL details may be limited)");
        }

        Console.WriteLine($"Service name:  {opt.ServiceName}");
        Console.WriteLine($"Service state: {await ServiceControl.QueryStateAsync(opt.ServiceName).ConfigureAwait(false)}");
        Console.WriteLine($"Config file:   {AgentPaths.MachineSettingsFile}");
        Console.WriteLine($"Config exists: {File.Exists(AgentPaths.MachineSettingsFile)}");
        Console.WriteLine($"Logs:          {AgentPaths.LogsDirectory}");

        string? relayUrl = null;
        string? token = null;
        string? signToolPath = null;

        if (File.Exists(AgentPaths.MachineSettingsFile))
        {
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(AgentPaths.MachineSettingsFile).ConfigureAwait(false));
                if (doc.RootElement.TryGetProperty("SignRelayAgent", out var section))
                {
                    relayUrl = section.TryGetProperty("RelayUrl", out var u) ? u.GetString() : null;
                    token = section.TryGetProperty("AgentToken", out var t) ? t.GetString() : null;
                    signToolPath = section.TryGetProperty("SignToolPath", out var s) ? s.GetString() : null;
                    if (section.TryGetProperty("AgentId", out var id))
                        Console.WriteLine($"AgentId:       {id.GetString()}");
                    if (section.TryGetProperty("SigningExecution", out var mode))
                        Console.WriteLine($"Signing mode:  {mode.GetString()}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not read machine settings: {ex.Message}");
            }
        }

        Console.WriteLine($"RelayUrl:      {relayUrl ?? "(not set)"}");

        if (SignToolCommandBuilder.TryResolveDirectSignTool(signToolPath, out var direct))
            Console.WriteLine($"signtool:      {direct} (direct)");
        else if (SignToolCommandBuilder.TryResolveWdkWhere(out var wdk, out var needsCmd))
            Console.WriteLine($"signtool:      via wdkwhere ({wdk}{(needsCmd ? " via cmd.exe" : "")})");
        else
            Console.WriteLine("signtool:      NOT FOUND (set SignToolPath, PATH, or install wdkwhere)");

        if (!string.IsNullOrWhiteSpace(relayUrl))
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var baseUrl = relayUrl.TrimEnd('/') + "/";
                var healthUri = new Uri(new Uri(baseUrl), "health");
                using var response = await http
                    .GetAsync(healthUri, HttpCompletionOption.ResponseHeadersRead)
                    .ConfigureAwait(false);
                Console.WriteLine($"Health:        {(int)response.StatusCode} {response.ReasonPhrase} ({healthUri})");
                if (!response.IsSuccessStatusCode)
                {
                    var snippet = await ReadBoundedTextAsync(response.Content, maxChars: 120).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(snippet))
                        Console.WriteLine($"Health body:   {snippet.Replace('\r', ' ').Replace('\n', ' ').Trim()}");
                    Console.WriteLine(
                        "Hint:          Non-2xx from /health often means the reverse proxy has no router " +
                        "(e.g. Traefik dropped an unhealthy container). Check the relay container health on the VPS.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Health:        FAILED — {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(token))
            Console.WriteLine("AgentToken:    (configured)");
        else
            Console.WriteLine("AgentToken:    (missing)");

        return 0;
    }

    [SupportedOSPlatform("windows")]
    internal static void WriteMachineSettings(InstallOptions opt)
    {
        Directory.CreateDirectory(AgentPaths.RootDirectory);
        Directory.CreateDirectory(AgentPaths.LogsDirectory);
        Directory.CreateDirectory(AgentPaths.DefaultJobStagingRoot);

        var section = new JsonObject
        {
            ["RelayUrl"] = opt.RelayUrl!.Trim().TrimEnd('/'),
            ["AgentToken"] = opt.Token,
        };

        if (!string.IsNullOrWhiteSpace(opt.AgentId))
            section["AgentId"] = opt.AgentId.Trim();
        if (!string.IsNullOrWhiteSpace(opt.Thumbprint))
            section["CertificateThumbprint"] = opt.Thumbprint.Trim();
        if (!string.IsNullOrWhiteSpace(opt.SubjectName))
            section["CertificateSubjectName"] = opt.SubjectName.Trim();
        if (!string.IsNullOrWhiteSpace(opt.TimestampUrl))
            section["TimestampServerUrl"] = opt.TimestampUrl.Trim();
        if (!string.IsNullOrWhiteSpace(opt.SignTool))
            section["SignToolPath"] = opt.SignTool.Trim();
        if (!string.IsNullOrWhiteSpace(opt.SigningExecution))
            section["SigningExecution"] = opt.SigningExecution.Trim();

        var root = new JsonObject { ["SignRelayAgent"] = section };
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(AgentPaths.MachineSettingsFile, json + Environment.NewLine);

        RestrictToSystemAndAdministrators(AgentPaths.MachineSettingsFile);
        RestrictToSystemAndAdministrators(AgentPaths.RootDirectory);
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictToSystemAndAdministrators(string path)
    {
        var isDir = Directory.Exists(path);
        if (isDir)
        {
            var di = new DirectoryInfo(path);
            var acl = di.GetAccessControl();
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in rules)
                acl.RemoveAccessRule(rule);

            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            acl.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            di.SetAccessControl(acl);
        }
        else
        {
            var fi = new FileInfo(path);
            var acl = fi.GetAccessControl();
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in rules)
                acl.RemoveAccessRule(rule);

            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            acl.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
            fi.SetAccessControl(acl);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string? PromptIfMissing(string? value, string label, bool required, bool secret = false)
    {
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        if (!Environment.UserInteractive || Console.IsInputRedirected)
            return value;

        Console.Write($"{label}: ");
        if (!secret)
            return Console.ReadLine()?.Trim();

        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0)
                    chars.RemoveAt(chars.Count - 1);
                continue;
            }

            if (!char.IsControl(key.KeyChar))
                chars.Add(key.KeyChar);
        }

        var entered = new string(chars.ToArray());
        return required && string.IsNullOrWhiteSpace(entered) ? null : entered;
    }

    private static string RequireValue(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
            throw new ArgumentException($"Missing value for {flag}.");
        return args[++i];
    }

    /// <summary>
    /// Reads at most <paramref name="maxChars"/> characters from the response body.
    /// Appends an ellipsis when more content was available.
    /// </summary>
    private static async Task<string> ReadBoundedTextAsync(HttpContent content, int maxChars)
    {
        await using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 256,
            leaveOpen: true);
        var buffer = new char[maxChars + 1];
        var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
        if (read <= 0)
            return string.Empty;
        if (read <= maxChars)
            return new string(buffer, 0, read);
        return new string(buffer, 0, maxChars) + "…";
    }

    private static void PrintInstallHelp()
    {
        Console.WriteLine("""
            SignRelay.Agent.exe install [options]

            Options:
              --relay-url <url>           Public base URL of the relay (required)
              --token <token>             Agent bearer token (required; matches SignRelay__AgentToken)
              --agent-id <id>             Optional agent identifier reported on lease
              --thumbprint <sha1>         Certificate SHA1 thumbprint (signtool /sha1)
              --subject-name <name>       Certificate subject name (signtool /n)
              --timestamp-url <url>       RFC 3161 timestamp server URL
              --signtool <path>           Full path to signtool.exe
              --signing-execution <mode>  Auto | SameProcess | InteractiveUser
              --service-name <name>       Windows service name (default: SignRelayAgent)
              --start                     Start the service after install
              -h, --help                  Show this help

            Writes machine settings to %ProgramData%\SignRelay\Agent\agent.settings.json
            and registers a LocalSystem delayed-auto service.
            """);
    }

    private static void PrintUninstallHelp()
    {
        Console.WriteLine("""
            SignRelay.Agent.exe uninstall [options]

            Options:
              --service-name <name>  Windows service name (default: SignRelayAgent)
              --purge                Also delete %ProgramData%\SignRelay\Agent
              -h, --help             Show this help
            """);
    }

    private static void PrintStatusHelp()
    {
        Console.WriteLine("""
            SignRelay.Agent.exe status [options]

            Options:
              --service-name <name>  Windows service name (default: SignRelayAgent)
              -h, --help             Show this help
            """);
    }
}
