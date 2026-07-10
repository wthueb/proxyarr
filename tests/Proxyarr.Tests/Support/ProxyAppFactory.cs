using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Proxyarr.Tests.Support;

/// <summary>Boots the proxy in-process against a YAML config supplied as a string.</summary>
public sealed class ProxyAppFactory : WebApplicationFactory<Program>
{
    private readonly string _configPath;
    private readonly ILoggerProvider? _loggerProvider;
    private readonly Action<IServiceCollection>? _configureServices;

    public ProxyAppFactory(
        string configYaml,
        ILoggerProvider? loggerProvider = null,
        Action<IServiceCollection>? configureServices = null
    )
    {
        _configPath = Path.Combine(Path.GetTempPath(), $"proxyarr-tests-{Guid.NewGuid():N}.yml");
        File.WriteAllText(_configPath, configYaml);
        _loggerProvider = loggerProvider;
        _configureServices = configureServices;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("config", _configPath);

        // ConfigureTestServices runs after Program's own registrations, so the capturing
        // provider survives the ClearProviders() call in Program and extra registrations
        // (e.g. stub adapters) land on top of the real ones.
        builder.ConfigureTestServices(services =>
        {
            if (_loggerProvider is not null)
            {
                services.AddSingleton(_loggerProvider);
            }

            _configureServices?.Invoke(services);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        File.Delete(_configPath);
    }
}
