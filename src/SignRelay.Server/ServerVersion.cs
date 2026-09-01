using System.Reflection;

namespace SignRelay.Server;

/// <summary>
/// Resolves the running server's MinVer-stamped version from assembly metadata.
/// </summary>
internal static class ServerVersion
{
    internal static string Current { get; } = Resolve(typeof(ServerVersion).Assembly);

    internal static string Resolve(Assembly assembly)
    {
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
            return assembly.GetName().Version?.ToString() ?? "0.0.0";

        return StripBuildMetadata(informational);
    }

    internal static string StripBuildMetadata(string version)
    {
        var plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }
}
