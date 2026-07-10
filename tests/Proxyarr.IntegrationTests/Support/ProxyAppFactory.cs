using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Proxyarr.IntegrationTests.Support;

/// <summary>Boots the proxy in-process against a YAML config supplied as a string.</summary>
public sealed class ProxyAppFactory : WebApplicationFactory<Program>
{
    private readonly string _configPath;

    public ProxyAppFactory(string configYaml)
    {
        _configPath = Path.Combine(Path.GetTempPath(), $"proxyarr-tests-{Guid.NewGuid():N}.yml");
        File.WriteAllText(_configPath, configYaml);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("config", _configPath);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        File.Delete(_configPath);
    }
}
