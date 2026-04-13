using FastEndpoints;
using FastEndpoints.Swagger;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using SignRelay.Contracts;
using SignRelay.Server.Auth;
using SignRelay.Server.Data;
using SignRelay.Server.Options;
using SignRelay.Server.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SignRelayOptions>(builder.Configuration.GetSection(SignRelayOptions.SectionName));
builder.Services.AddDbContext<AppDbContext>(o =>
{
    var dataDir = builder.Configuration.GetValue<string>("SignRelay:StoragePath") ?? "data";
    var dbPath = Path.Combine(dataDir, "signrelay.db");
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
    o.UseSqlite($"Data Source={dbPath}");
});

builder.Services.AddSingleton<JobEventHub>();
builder.Services.AddScoped<JobService>();
builder.Services.AddHostedService<JobSweeper>();

builder.Services
    .AddAuthentication(SignRelayAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, SignRelayAuthenticationHandler>(
        SignRelayAuthenticationHandler.SchemeName,
        _ => { });

builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("Ci", p => p.RequireClaim(SignRelayClaimTypes.Role, SignRelayClaimTypes.Ci));
    o.AddPolicy("Agent", p => p.RequireClaim(SignRelayClaimTypes.Role, SignRelayClaimTypes.Agent));
});

builder.Services.AddFastEndpoints();
builder.Services.SwaggerDocument();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync().ConfigureAwait(false);
}

var storage = app.Configuration.GetValue<string>("SignRelay:StoragePath") ?? "data";
Directory.CreateDirectory(Path.GetFullPath(storage));

app.UseAuthentication();
app.UseAuthorization();
app.UseFastEndpoints();
app.UseSwaggerGen();

app.Run();
