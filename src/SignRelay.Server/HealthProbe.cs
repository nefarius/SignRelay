namespace SignRelay.Server;

/// <summary>
/// Process-level health probe used by the Docker HEALTHCHECK.
/// Contacts the already-running server on loopback; does not start the host.
/// </summary>
internal static class HealthProbe
{
    public static async Task<int> RunAsync()
    {
        try
        {
            var port = ResolvePort();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await http
                .GetAsync(new Uri($"http://127.0.0.1:{port}/health"))
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>
    /// Resolves the listen port from ASPNETCORE_HTTP_PORTS, then ASPNETCORE_URLS, else 8080.
    /// </summary>
    internal static int ResolvePort()
    {
        var httpPorts = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS");
        if (!string.IsNullOrWhiteSpace(httpPorts))
        {
            foreach (var part in httpPorts.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(part, out var port) && port is > 0 and <= 65535)
                    return port;
            }
        }

        var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        if (!string.IsNullOrWhiteSpace(urls))
        {
            foreach (var raw in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // Uri does not accept +, *; normalize host for port parsing only.
                var candidate = raw
                    .Replace("://+", "://localhost", StringComparison.Ordinal)
                    .Replace("://*", "://localhost", StringComparison.Ordinal);
                if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                    && uri.Port is > 0 and <= 65535)
                    return uri.Port;
            }
        }

        return 8080;
    }
}
