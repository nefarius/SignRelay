using SignRelay.Agent;
using SignRelay.Agent.Options;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
builder.Services.AddSingleton<SignToolRunner>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
