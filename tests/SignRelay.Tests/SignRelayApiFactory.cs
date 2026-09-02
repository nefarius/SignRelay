using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using SignRelay.Server.Services;

namespace SignRelay.Tests;

public sealed class SignRelayApiFactory : WebApplicationFactory<JobService>
{
    public string StoragePath { get; } = Path.Combine(Path.GetTempPath(), "signrelay-api-" + Guid.NewGuid().ToString("N"));
    public string CiToken { get; } = new string('c', 32);
    public string AgentToken { get; } = new string('a', 32);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(StoragePath);
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SignRelay:CiToken"] = CiToken,
                ["SignRelay:AgentToken"] = AgentToken,
                ["SignRelay:StoragePath"] = StoragePath,
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(StoragePath, recursive: true); } catch { /* best effort */ }
    }
}
