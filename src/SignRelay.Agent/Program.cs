using Microsoft.Extensions.Hosting.WindowsServices;
using SignRelay.Agent;
using SignRelay.Agent.Options;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "SignRelay Agent");
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));

// Named HttpClient for agent-level calls (lease polling) — uses the agent token
builder.Services.AddHttpClient("SignRelayAgent", (sp, client) =>
{
    var opt = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value;
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
    var opt = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value;
    client.BaseAddress = new Uri(opt.RelayUrl.TrimEnd('/') + "/");
    client.Timeout = opt.LeaseDuration > TimeSpan.Zero ? opt.LeaseDuration : TimeSpan.FromMinutes(30);
});

builder.Services.AddSingleton<SignToolRunner>();
builder.Services.AddSingleton<InteractiveUserProcessLauncher>();
builder.Services.AddSingleton<IJobStaging, JobStaging>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
