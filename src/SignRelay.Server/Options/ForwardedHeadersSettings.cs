namespace SignRelay.Server.Options;

/// <summary>
/// Configuration for <see cref="Microsoft.AspNetCore.Builder.ForwardedHeadersOptions"/> (see appsettings <c>ForwardedHeaders</c>).
/// </summary>
public sealed class ForwardedHeadersSettings
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>
    /// When true, trusts forwarding headers from senders whose IP falls on a network derived from this host's interfaces
    /// (Docker bridge, LAN, etc.). Do not use when the app is directly exposed to the internet without a reverse proxy.
    /// </summary>
    public bool AutoDetectPrivateNetworks { get; set; } = true;

    /// <summary>
    /// Clears known proxy restrictions so any remote may supply forwarded headers (e.g. some Kubernetes ingress setups).
    /// Mutually exclusive with <see cref="AutoDetectPrivateNetworks"/> in configuration validation.
    /// </summary>
    public bool AllowFromAny { get; set; }
}
