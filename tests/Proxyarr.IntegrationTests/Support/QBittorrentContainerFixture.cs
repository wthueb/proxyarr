using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Proxyarr.IntegrationTests.Support;

/// <summary>Runs a real qBittorrent 5.2.3 in Docker, shared by all tests in a class.</summary>
public sealed class QBittorrentContainerFixture : IAsyncLifetime
{
    public const string Image = "linuxserver/qbittorrent:5.2.3";

    // Auth is bypassed for all source subnets so tests don't depend on the random password the
    // container generates on first start; logging in through the proxy still succeeds.
    private const string QBittorrentConf = """
        [LegalNotice]
        Accepted=true

        [Preferences]
        WebUI\AuthSubnetWhitelist=0.0.0.0/0
        WebUI\AuthSubnetWhitelistEnabled=true
        WebUI\CSRFProtection=false
        WebUI\HostHeaderValidation=false
        """;

    private IContainer? _container;

    /// <summary>Null when the container is running, otherwise the reason tests must skip.</summary>
    public string? SkipReason { get; private set; }

    public string UpstreamUrl { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        SkipReason = DockerProbe.SkipReason;
        if (SkipReason is not null)
        {
            return;
        }

        _container = new ContainerBuilder(Image)
            .WithPortBinding(8080, assignRandomHostPort: true)
            .WithEnvironment("TZ", "Etc/UTC")
            .WithResourceMapping(
                Encoding.UTF8.GetBytes(QBittorrentConf),
                "/config/qBittorrent/qBittorrent.conf"
            )
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(request =>
                        request.ForPort(8080).ForPath("/api/v2/app/version")
                    )
            )
            .Build();

        await _container.StartAsync();
        UpstreamUrl = $"http://127.0.0.1:{_container.GetMappedPublicPort(8080)}";
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
