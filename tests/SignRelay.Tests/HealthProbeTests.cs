using SignRelay.Server;

namespace SignRelay.Tests;

public class HealthProbeTests
{
    [Fact]
    public void ResolvePort_defaults_to_8080_when_unset()
    {
        using var _http = new EnvVarScope("ASPNETCORE_HTTP_PORTS", null);
        using var _urls = new EnvVarScope("ASPNETCORE_URLS", null);
        Assert.Equal(8080, HealthProbe.ResolvePort());
    }

    [Fact]
    public void ResolvePort_prefers_ASPNETCORE_HTTP_PORTS()
    {
        using var _http = new EnvVarScope("ASPNETCORE_HTTP_PORTS", "9090");
        using var _urls = new EnvVarScope("ASPNETCORE_URLS", "http://0.0.0.0:8080");
        Assert.Equal(9090, HealthProbe.ResolvePort());
    }

    [Fact]
    public void ResolvePort_parses_ASPNETCORE_URLS()
    {
        using var _http = new EnvVarScope("ASPNETCORE_HTTP_PORTS", null);
        using var _urls = new EnvVarScope("ASPNETCORE_URLS", "http://0.0.0.0:8080");
        Assert.Equal(8080, HealthProbe.ResolvePort());
    }

    private sealed class EnvVarScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvVarScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
