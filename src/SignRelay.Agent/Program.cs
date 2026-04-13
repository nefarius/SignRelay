using Microsoft.Extensions.Hosting.WindowsServices;
using SignRelay.Agent;
using SignRelay.Agent.Options;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "SignRelay Agent");
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
builder.Services.AddSingleton<SignToolRunner>();
builder.Services.AddSingleton<InteractiveUserProcessLauncher>();
builder.Services.AddSingleton<IJobStaging, JobStaging>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
