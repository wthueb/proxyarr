using Proxyarr.Configuration;
using Proxyarr.Dedupe;

namespace Proxyarr.Tests;

public class ConfigLoaderTests
{
    [Fact]
    public void Parses_a_dedupe_block()
    {
        var config = ConfigLoader.Parse(
            """
            database: /data/proxyarr.db
            clients:
              - name: radarr1
                type: qbittorrent
                upstream: http://qbit:8080
                dedupe:
                  enabled: true
                  category: proxyarr
                  group: main
              - name: sab1
                type: sabnzbd
                upstream: http://sab:8080
                dedupe:
                  enabled: true
                  announce_categories: [movies, tv]
            """
        );

        Assert.Equal("/data/proxyarr.db", config.Database);
        Assert.True(config.Clients[0].DedupeEnabled);
        Assert.Equal("proxyarr", config.Clients[0].Dedupe!.Category);
        Assert.Equal("main", config.Clients[0].Dedupe!.Group);
        Assert.Equal(["movies", "tv"], config.Clients[1].Dedupe!.AnnounceCategories);
    }

    [Fact]
    public void Dedupe_is_off_by_default()
    {
        var config = ConfigLoader.Parse(
            """
            clients:
              - name: qbit
                type: qbittorrent
                upstream: http://localhost:8080
            """
        );

        Assert.Null(config.Clients[0].Dedupe);
        Assert.False(config.Clients[0].DedupeEnabled);
    }

    [Fact]
    public void Dedupe_sub_keys_without_enabled_are_rejected()
    {
        var ex = Assert.Throws<ConfigurationException>(() =>
            ConfigLoader.Parse(
                """
                clients:
                  - name: qbit
                    type: qbittorrent
                    upstream: http://localhost:8080
                    dedupe:
                      category: proxyarr
                """
            )
        );

        Assert.Contains("enabled", ex.Message);
    }

    [Fact]
    public void Announce_categories_on_a_non_sabnzbd_client_are_rejected()
    {
        var ex = Assert.Throws<ConfigurationException>(() =>
            ConfigLoader.Parse(
                """
                clients:
                  - name: qbit
                    type: qbittorrent
                    upstream: http://localhost:8080
                    dedupe:
                      enabled: true
                      announce_categories: [movies]
                """
            )
        );

        Assert.Contains("announce_categories", ex.Message);
    }

    [Fact]
    public void Group_members_must_agree_on_category()
    {
        var ex = Assert.Throws<ConfigurationException>(() =>
            ConfigLoader.Parse(
                """
                clients:
                  - name: radarr1
                    type: qbittorrent
                    upstream: http://qbit:8080
                    dedupe:
                      enabled: true
                      category: alpha
                  - name: radarr2
                    type: qbittorrent
                    upstream: http://qbit:8080
                    dedupe:
                      enabled: true
                      category: beta
                """
            )
        );

        Assert.Contains("category", ex.Message);
    }

    [Fact]
    public void Groups_are_derived_from_the_shared_upstream_url()
    {
        var config = ConfigLoader.Parse(
            """
            clients:
              - name: radarr1
                type: qbittorrent
                upstream: http://qbit:8080/
                dedupe:
                  enabled: true
                  category: proxyarr
              - name: radarr2
                type: qbittorrent
                upstream: http://qbit:8080
                dedupe:
                  enabled: true
                  category: proxyarr
              - name: lonely
                type: qbittorrent
                upstream: http://other:8080
                dedupe:
                  enabled: true
                  category: proxyarr
            """
        );

        var groups = DedupeGroups.Build(config);
        var shared = groups.For(config.Clients[0]);
        Assert.NotNull(shared);
        Assert.Same(shared, groups.For(config.Clients[1]));
        Assert.Equal(["radarr1", "radarr2"], shared!.Members.Select(m => m.Name).Order());
        Assert.NotSame(shared, groups.For(config.Clients[2]));
    }

    [Fact]
    public void The_group_override_merges_instances_on_different_hostnames()
    {
        var config = ConfigLoader.Parse(
            """
            clients:
              - name: radarr1
                type: qbittorrent
                upstream: http://qbit-a:8080
                dedupe:
                  enabled: true
                  category: proxyarr
                  group: shared
              - name: radarr2
                type: qbittorrent
                upstream: http://qbit-b:8080
                dedupe:
                  enabled: true
                  category: proxyarr
                  group: shared
            """
        );

        var groups = DedupeGroups.Build(config);
        Assert.Same(groups.For(config.Clients[0]), groups.For(config.Clients[1]));
    }

    [Fact]
    public void Non_dedupe_instances_have_no_group()
    {
        var config = ConfigLoader.Parse(
            """
            clients:
              - name: qbit
                type: qbittorrent
                upstream: http://localhost:8080
            """
        );

        Assert.Null(DedupeGroups.Build(config).For(config.Clients[0]));
    }

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
              overrides:
                Microsoft.AspNetCore: warning
                Yarp: error
            clients: []
            """
        );

        Assert.True(config.Logging.UsesJson);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Debug, config.Logging.ParsedLevel);
        Assert.Equal(
            Microsoft.Extensions.Logging.LogLevel.Warning,
            config.Logging.ParsedOverrides.Single(o => o.Key == "Microsoft.AspNetCore").Value
        );
    }

    [Fact]
    public void Include_scopes_option_is_rejected_because_scopes_are_always_enabled()
    {
        var ex = Assert.Throws<ConfigurationException>(() =>
            ConfigLoader.Parse(
                """
                logging:
                  include_scopes: false
                """
            )
        );

        Assert.Contains("Failed to parse", ex.Message);
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
