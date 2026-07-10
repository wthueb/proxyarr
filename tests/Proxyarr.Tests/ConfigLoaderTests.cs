using Proxyarr.Configuration;

namespace Proxyarr.Tests;

public class ConfigLoaderTests
{
    [Fact]
    public void Parses_a_full_config()
    {
        var config = ConfigLoader.Parse(
            """
            server:
              host: 127.0.0.1
              port: 9999

            clients:
              - name: qbittorrent
                type: qbittorrent
                upstream: http://localhost:8080

              - name: sabnzbd
                type: sabnzbd
                upstream: http://localhost:8085/sabnzbd
            """
        );

        Assert.Equal("127.0.0.1", config.Server.Host);
        Assert.Equal(9999, config.Server.Port);
        Assert.Equal(2, config.Clients.Count);
        Assert.Equal("qbittorrent", config.Clients[0].Name);
        Assert.Equal("http://localhost:8080", config.Clients[0].Upstream);
        Assert.Equal("sabnzbd", config.Clients[1].Type);
        Assert.Equal("http://localhost:8085/sabnzbd", config.Clients[1].Upstream);
    }

    [Fact]
    public void Applies_defaults_when_server_section_is_omitted()
    {
        var config = ConfigLoader.Parse(
            """
            clients:
              - name: qbit
                type: qbittorrent
                upstream: http://localhost:8080
            """
        );

        Assert.Equal("0.0.0.0", config.Server.Host);
        Assert.Equal(8484, config.Server.Port);
    }

    [Fact]
    public void Trims_trailing_slash_from_upstream()
    {
        var config = ConfigLoader.Parse(
            """
            clients:
              - name: qbit
                type: qbittorrent
                upstream: http://localhost:8080/
            """
        );

        Assert.Equal("http://localhost:8080", config.Clients[0].Upstream);
    }

    [Fact]
    public void Missing_file_reports_the_path_and_how_to_configure_it()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.yml");

        var ex = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(missing));

        Assert.Contains(missing, ex.Message);
        Assert.Contains("PROXYARR_CONFIG", ex.Message);
    }

    [Fact]
    public void Unknown_keys_are_rejected_to_catch_typos()
    {
        var ex = Assert.Throws<ConfigurationException>(() =>
            ConfigLoader.Parse(
                """
                server:
                  prot: 1234
                """
            )
        );

        Assert.Contains("Failed to parse", ex.Message);
    }

    [Fact]
    public void Malformed_yaml_is_rejected()
    {
        Assert.Throws<ConfigurationException>(() => ConfigLoader.Parse("clients: ["));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Qbit")]
    [InlineData("my client")]
    [InlineData("client/1")]
    [InlineData("-qbit")]
    public void Invalid_instance_names_are_rejected(string name)
    {
        var ex = Assert.Throws<ConfigurationException>(() =>
            ConfigLoader.Parse(
                $"""
                clients:
                  - name: "{name}"
                    type: qbittorrent
                    upstream: http://localhost:8080
                """
            )
        );

        Assert.Contains("invalid", ex.Message);
    }

    [Fact]
    public void Reserved_instance_names_are_rejected()
    {
        var ex = Assert.Throws<ConfigurationException>(() =>
            ConfigLoader.Parse(
                """
                clients:
                  - name: healthz
                    type: qbittorrent
                    upstream: http://localhost:8080
                """
            )
        );

        Assert.Contains("reserved", ex.Message);
    }

    [Fact]
    public void Duplicate_instance_names_are_rejected()
    {
        var ex = Assert.Throws<ConfigurationException>(() =>
            ConfigLoader.Parse(
                """
                clients:
                  - name: qbit
                    type: qbittorrent
                    upstream: http://localhost:8080
                  - name: qbit
                    type: sabnzbd
                    upstream: http://localhost:8085
                """
            )
        );

        Assert.Contains("Duplicate", ex.Message);
    }

    [Theory]
    [InlineData("localhost:8080")]
    [InlineData("ftp://localhost")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Invalid_upstreams_are_rejected(string upstream)
    {
        var ex = Assert.Throws<ConfigurationException>(() =>
            ConfigLoader.Parse(
                $"""
                clients:
                  - name: qbit
                    type: qbittorrent
                    upstream: "{upstream}"
                """
            )
        );

        Assert.Contains("upstream", ex.Message);
    }

    [Fact]
    public void Missing_type_is_rejected()
    {
        var ex = Assert.Throws<ConfigurationException>(() =>
            ConfigLoader.Parse(
                """
                clients:
                  - name: qbit
                    upstream: http://localhost:8080
                """
            )
        );

        Assert.Contains("type", ex.Message);
    }

    [Fact]
    public void Logging_defaults_to_logfmt_at_information()
    {
        var config = ConfigLoader.Parse("clients: []");

        Assert.Equal("logfmt", config.Logging.Format);
        Assert.False(config.Logging.UsesJson);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, config.Logging.ParsedLevel);
        Assert.False(config.Logging.IncludeScopes);
        Assert.Empty(config.Logging.Overrides);
    }

    [Fact]
    public void Logging_section_is_parsed()
    {
        var config = ConfigLoader.Parse(
            """
            logging:
              level: debug
              format: json
              include_scopes: true
              overrides:
                Microsoft.AspNetCore: warning
                Yarp: error
            clients: []
            """
        );

        Assert.True(config.Logging.UsesJson);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Debug, config.Logging.ParsedLevel);
        Assert.True(config.Logging.IncludeScopes);
        Assert.Equal(
            Microsoft.Extensions.Logging.LogLevel.Warning,
            config.Logging.ParsedOverrides.Single(o => o.Key == "Microsoft.AspNetCore").Value
        );
    }

    [Fact]
    public void Unknown_log_formats_are_rejected()
    {
        var ex = Assert.Throws<ConfigurationException>(() =>
            ConfigLoader.Parse(
                """
                logging:
                  format: xml
                """
            )
        );

        Assert.Contains("log format", ex.Message);
    }

    [Theory]
    [InlineData("verbose")]
    [InlineData("")]
    public void Unknown_log_levels_are_rejected(string level)
    {
        var ex = Assert.Throws<ConfigurationException>(() =>
            ConfigLoader.Parse(
                $"""
                logging:
                  level: "{level}"
                """
            )
        );

        Assert.Contains("log level", ex.Message);
    }

    [Fact]
    public void Invalid_override_levels_are_rejected()
    {
        Assert.Throws<ConfigurationException>(() =>
            ConfigLoader.Parse(
                """
                logging:
                  overrides:
                    Microsoft: loud
                """
            )
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Out_of_range_ports_are_rejected(int port)
    {
        var ex = Assert.Throws<ConfigurationException>(() =>
            ConfigLoader.Parse(
                $"""
                server:
                  port: {port}
                """
            )
        );

        Assert.Contains("server.port", ex.Message);
    }
}
