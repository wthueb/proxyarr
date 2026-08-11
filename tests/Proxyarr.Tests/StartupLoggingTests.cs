using System.Reflection;
using Microsoft.Extensions.Logging;
using Proxyarr.Configuration;
using Proxyarr.Tests.Support;

namespace Proxyarr.Tests;

public sealed class StartupLoggingTests
{
    [Fact]
    public void Startup_logs_the_current_version()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = new ProxyAppFactory(
            """
            clients:
              qbittorrent:
                upstreams:
                  - name: main
                    url: http://localhost:8080
                instances:
                  - name: qbit
                    upstream: main
            """,
            logs
        );
        using var client = factory.CreateClient();

        var expectedVersion = typeof(ConfigLoader)
            .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;
        var versionLog = Assert.Single(
            logs.Events,
            logEvent =>
                logEvent.Level == LogLevel.Information && logEvent.Message == "Proxyarr started"
        );

        Assert.Equal("Proxyarr", versionLog.Category);
        Assert.Equal(expectedVersion, versionLog.Fields["Version"]);
    }
}
