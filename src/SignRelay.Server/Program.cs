using FastEndpoints;
using FastEndpoints.Swagger;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using SignRelay.Contracts;
using SignRelay.Server.Auth;
using SignRelay.Server.Data;
using SignRelay.Server.Networking;
using SignRelay.Server.Options;
using SignRelay.Server.Services;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, _, configuration) =>
    {
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .WriteTo.Console(
                theme: AnsiConsoleTheme.Literate,
                applyThemeToRedirectedOutput: true);
    });

    builder.Services.Configure<ForwardedHeadersOptions>(options =>
        ForwardedHeadersConfiguration.Apply(builder.Configuration, options));

    builder.Services
        .AddOptions<SignRelayOptions>()
        .Bind(builder.Configuration.GetSection(SignRelayOptions.SectionName))
        .ValidateOnStart();
    builder.Services.AddSingleton<IValidateOptions<SignRelayOptions>, SignRelayOptionsValidator>();

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
        o.AddPolicy("Lease", p => p.RequireClaim(SignRelayClaimTypes.Role, SignRelayClaimTypes.Lease));
    });

    builder.Services.AddFastEndpoints();
    builder.Services.AddHealthChecks();

    if (builder.Environment.IsDevelopment())
        builder.Services.SwaggerDocument();

    // Request size limits — derived from MaxTotalJobBytes (×2 to cover signed uploads).
    // Both Kestrel and multipart FormOptions must be raised; ASP.NET Core's default
    // MultipartBodyLengthLimit is 128 MiB and would otherwise silently cap jobs below
    // the configured MaxTotalJobBytes (default 512 MiB).
    var maxJobBytes = builder.Configuration
        .GetSection(SignRelayOptions.SectionName)
        .GetValue<long>("MaxTotalJobBytes", 512L * 1024 * 1024);
    var maxRequestBytes = maxJobBytes * 2;

    builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = maxRequestBytes);
    builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = maxRequestBytes);

    var app = builder.Build();

    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync().ConfigureAwait(false);
    }

    var storage = app.Configuration.GetValue<string>("SignRelay:StoragePath") ?? "data";
    Directory.CreateDirectory(Path.GetFullPath(storage));

    app.UseForwardedHeaders();
    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate =
            "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms from {ClientIP}";
        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            diagnosticContext.Set("ClientIP", httpContext.Connection.RemoteIpAddress?.ToString());
        };
    });

    app.UseAuthentication();
    app.UseAuthorization();
    app.UseFastEndpoints();
    app.MapHealthChecks("/health");

    if (app.Environment.IsDevelopment())
        app.UseSwaggerGen();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
