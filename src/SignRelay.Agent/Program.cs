using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Options;
using Serilog;
using SignRelay.Agent;
using SignRelay.Agent.Options;
using SignRelay.Agent.Service;

if (InteractiveConsoleExec.IsVerb(args))
{
    if (OperatingSystem.IsWindows())
        return InteractiveConsoleExec.Run(args);
    return 2;
}

if (ServiceCommands.IsServiceVerb(args))
    return await ServiceCommands.RunAsync(args).ConfigureAwait(false);

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Directory.CreateDirectory(AgentPaths.LogsDirectory);

    var builder = Host.CreateApplicationBuilder(args);

    builder.Configuration.AddJsonFile(
        AgentPaths.MachineSettingsFile,
        optional: true,
        reloadOnChange: true);

    builder.Services.AddWindowsService(o => o.ServiceName = AgentPaths.DefaultServiceDisplayName);

    if (OperatingSystem.IsWindows() && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        ConfigureWindowsEventLog(builder);

    builder.Services.AddSerilog((services, configuration) =>
    {
        configuration
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(
                path: Path.Combine(AgentPaths.LogsDirectory, "agent-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true);
    });

    builder.Services
        .AddOptions<AgentOptions>()
        .Bind(builder.Configuration.GetSection(AgentOptions.SectionName))
        .ValidateOnStart();
    builder.Services.AddSingleton<IValidateOptions<AgentOptions>, AgentOptionsValidator>();

    // Named HttpClient for agent-level calls (lease polling) — uses the agent token
    builder.Services.AddHttpClient("SignRelayAgent", (sp, client) =>
    {
        var opt = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
        client.BaseAddress = new Uri(opt.RelayUrl.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opt.AgentToken);
    });

    // Named HttpClient for job-scoped calls — auth header is set per-job in Worker.
    // Timeout is set to LeaseDuration (default 30 min) so a completely hung transfer
    // cannot outlive the lease window. Operations that respect CancellationToken will
    // still be cancelled sooner when the lease or stop-token fires.
    builder.Services.AddHttpClient("SignRelayJob", (sp, client) =>
    {
        var opt = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
        client.BaseAddress = new Uri(opt.RelayUrl.TrimEnd('/') + "/");
        client.Timeout = opt.LeaseDuration > TimeSpan.Zero ? opt.LeaseDuration : TimeSpan.FromMinutes(30);
    });

    builder.Services.AddSingleton<SignToolRunner>();
    builder.Services.AddSingleton<InteractiveUserProcessLauncher>();
    builder.Services.AddSingleton<IJobStaging, JobStaging>();
    builder.Services.AddHostedService<Worker>();

    var host = builder.Build();
    await host.RunAsync().ConfigureAwait(false);
    return 0;
}
catch (OptionsValidationException ex)
{
    foreach (var failure in ex.Failures)
        Log.Fatal("Configuration error: {Failure}", failure);
    return 2;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}

[SupportedOSPlatform("windows")]
static void ConfigureWindowsEventLog(HostApplicationBuilder builder)
{
    // EventLogSettings setters are annotated windows-only; the Configure lambda is not
    // treated as reachable-only-on-windows by CA1416 even inside this method.
#pragma warning disable CA1416
    builder.Services.Configure<EventLogSettings>(settings =>
    {
        settings.SourceName = AgentPaths.EventLogSourceName;
        settings.LogName = "Application";
    });
#pragma warning restore CA1416
    builder.Logging.AddFilter<EventLogLoggerProvider>(null, LogLevel.Information);
    builder.Logging.AddFilter<EventLogLoggerProvider>("System.Net.Http.HttpClient", LogLevel.Warning);
}
