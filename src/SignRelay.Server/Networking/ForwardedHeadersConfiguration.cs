using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Serilog;
using SignRelay.Server.Options;
using SignRelay.Server.Util;

namespace SignRelay.Server.Networking;

internal static class ForwardedHeadersConfiguration
{
    private const string AzureInstanceEnv = "WEBSITE_INSTANCE_ID";

    public static void Apply(IConfiguration configuration, ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        var section = configuration.GetSection(ForwardedHeadersSettings.SectionName);
        var settings = section.Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings();
        var proxies = section.GetSection("KnownProxies").Get<string[]>() ?? [];
        var networks = section.GetSection("KnownNetworks").Get<string[]>() ?? [];
        var hasManual = proxies.Length > 0 || networks.Length > 0;

        if (settings.AllowFromAny && settings.AutoDetectPrivateNetworks)
            throw new InvalidOperationException(
                $"{ForwardedHeadersSettings.SectionName}:{nameof(ForwardedHeadersSettings.AllowFromAny)} cannot be true when " +
                $"{nameof(ForwardedHeadersSettings.AutoDetectPrivateNetworks)} is true.");

        if (settings.AllowFromAny)
        {
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            return;
        }

        var isAzure = Environment.GetEnvironmentVariable(AzureInstanceEnv) != null;
        if (isAzure && settings.AutoDetectPrivateNetworks)
        {
            Log.Warning(
                "Azure App Service detected ({Env} set); skipping ForwardedHeaders private-network auto-detection.",
                AzureInstanceEnv);
        }

        var runAuto = settings.AutoDetectPrivateNetworks && !isAzure;

        if (runAuto)
        {
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();

            foreach (var net in NetworkUtil.GetLocalNetworks())
            {
                Log.Information("Forwarded headers: trusting local network {Subnet}", net);
                options.KnownIPNetworks.Add(net);
            }
        }

        if (!runAuto && hasManual)
        {
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            AddManual(options, proxies, networks);
        }
        else if (runAuto && hasManual)
            AddManual(options, proxies, networks);
    }

    private static void AddManual(ForwardedHeadersOptions options, string[] proxies, string[] networks)
    {
        foreach (var p in proxies)
            options.KnownProxies.Add(IPAddress.Parse(p));

        foreach (var cidr in networks)
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
    }
}
