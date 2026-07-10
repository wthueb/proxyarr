using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Proxyarr.IntegrationTests.Support;

/// <summary>Runs a real SABnzbd 5.0.4 in Docker, shared by all tests in a class.</summary>
public sealed class SabnzbdContainerFixture : IAsyncLifetime
{
    public const string Image = "linuxserver/sabnzbd:5.0.4";

    public const string ApiKey = "proxyarrintegrationapikey0001";

    // inet_exposure 4 grants full API (with key) access to non-local addresses, which covers
    // requests arriving via the Docker port mapping.
    private const string SabnzbdIni = $"""
        [misc]
        api_key = {ApiKey}
        nzb_key = {ApiKey}
        host = 0.0.0.0
        port = 8080
        inet_exposure = 4
        """;

    private IContainer? _container;

    /// <summary>Null when the container is running, otherwise the reason tests must skip.</summary>
    public string? SkipReason { get; private set; }

    /// <summary>The linuxserver image serves SABnzbd at the root (no /sabnzbd URL base).</summary>
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
            .WithResourceMapping(Encoding.UTF8.GetBytes(SabnzbdIni), "/config/sabnzbd.ini")
            .Build();

        await _container.StartAsync();
        UpstreamUrl = $"http://127.0.0.1:{_container.GetMappedPublicPort(8080)}";

        // Readiness is checked by hand because the API probe needs a query string, which
        // HttpWaitStrategy's ForPath() would URL-escape into a different path.
        using var http = new HttpClient();
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            try
            {
                var response = await http.GetAsync($"{UpstreamUrl}/api?mode=version");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"SABnzbd did not become ready at {UpstreamUrl}.");
            }

            await Task.Delay(500);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
