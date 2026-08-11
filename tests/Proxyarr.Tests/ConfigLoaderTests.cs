using Proxyarr.Configuration;
using Proxyarr.Dedupe;

namespace Proxyarr.Tests;

public class ConfigLoaderTests
{
    [Fact]
    public void Parses_named_upstreams_groups_and_instances()
    {
        var config = ConfigLoader.Parse(
            """
            server:
              host: 0.0.0.0
              port: 8484
            logging:
              level: debug
            database: /data/proxyarr.db
            clients:
              qbittorrent:
                upstreams:
                  - name: main
                    url: http://qbit:8080/
                groups:
                  - name: radarr
                    category: radarr
                  - name: sonarr
                    category: sonarr
                instances:
                  - name: radarr
                    upstream: main
                    group: radarr
                  - name: radarr4k
                    upstream: main
                    group: radarr
                  - name: sonarr
                    upstream: main
                    group: sonarr
                  - name: sonarr4k
                    upstream: main
                    group: sonarr
              sabnzbd:
                upstreams:
                  - name: main
                    url: http://sabnzbd:9092
                groups:
                  - name: radarr
                    category: radarr
                  - name: sonarr
                    category: sonarr
                instances:
                  - name: radarr
                    upstream: main
                    group: radarr
                  - name: radarr4k
                    upstream: main
                    group: radarr
                  - name: sonarr
                    upstream: main
                    group: sonarr
                  - name: sonarr4k
                    upstream: main
                    group: sonarr
            """
        );

        Assert.Equal("/data/proxyarr.db", config.Database);
        Assert.Equal(8, config.ResolvedClients.Count);

        var radarr = Instance(config, "qbittorrent", "radarr");
        Assert.Equal("http://qbit:8080", radarr.Upstream);
        Assert.True(radarr.DedupeEnabled);
        Assert.Equal("radarr", radarr.Dedupe!.Group);
        Assert.Equal("radarr", radarr.Dedupe.Category);

        var sab = Instance(config, "sabnzbd", "radarr");
        Assert.Equal("http://sabnzbd:9092", sab.Upstream);
        Assert.Equal("radarr", sab.Dedupe!.Category);
    }

    [Fact]
    public void An_instance_without_a_group_is_pass_through()
    {
        var config = ConfigLoader.Parse(
            """
            clients:
              qbittorrent:
                upstreams:
                  - name: main
                    url: http://localhost:8080
                instances:
                  - name: direct
                    upstream: main
            """
        );

        var instance = Assert.Single(config.ResolvedClients);
        Assert.False(instance.DedupeEnabled);
        Assert.Null(instance.Dedupe);
        Assert.Null(DedupeGroups.Build(config).For(instance));
    }

    [Fact]
    public void Upstream_path_mappings_are_normalized_ordered_and_inherited()
    {
        var config = ConfigLoader.Parse(
            """
            clients:
              qbittorrent:
                upstreams:
                  - name: main
                    url: http://qbit:8080
                    path_mappings:
                      - from: /downloads/
                        to: /proxyarr/qbit/
                      - from: /downloads/special/
                        to: /proxyarr/qbit-special/
                instances:
                  - name: radarr
                    upstream: main
            """
        );

        var mappings = Assert.Single(config.ResolvedClients).PathMappings;
        Assert.Equal(2, mappings.Count);
        Assert.Equal("/downloads/special", mappings[0].From);
        Assert.Equal("/proxyarr/qbit-special", mappings[0].To);
        Assert.Equal("/downloads", mappings[1].From);
        Assert.Equal("/proxyarr/qbit", mappings[1].To);
    }

    [Theory]
    [InlineData("downloads", "/proxyarr/qbit", "from")]
    [InlineData("/downloads", "proxyarr/qbit", "to")]
    [InlineData("", "/proxyarr/qbit", "from")]
    [InlineData("/downloads", "", "to")]
    public void Relative_or_empty_path_mapping_roots_are_rejected(
        string from,
        string to,
        string field
    )
    {
        var ex = Assert.Throws<ConfigurationException>(() =>
            ConfigLoader.Parse(
                $$"""
                clients:
                  qbittorrent:
                    upstreams:
                      - name: main
                        url: http://qbit:8080
                        path_mappings:
                          - from: "{{from}}"
                            to: "{{to}}"
                """
            )
        );

        Assert.Contains($"'{field}' must be an absolute path", ex.Message);
    }

    [Fact]
    public void Duplicate_path_mapping_roots_are_rejected()
    {
        var ex = Assert.Throws<ConfigurationException>(() =>
            ConfigLoader.Parse(
                """
                clients:
                  sabnzbd:
                    upstreams:
                      - name: main
                        url: http://sab:8080
                        path_mappings:
                          - from: C:\Downloads
                            to: /proxyarr/sab-a
                          - from: c:/downloads/
                            to: /proxyarr/sab-b
                """
            )
        );

        Assert.Contains("duplicate path mapping", ex.Message);
    }

    [Fact]
    public void Named_groups_are_the_dedupe_boundary()
    {
        var config = ConfigLoader.Parse(
            """
            clients:
              qbittorrent:
                upstreams:
                  - name: main
                    url: http://qbit:8080
                groups:
                  - name: radarr
                    category: radarr
                  - name: sonarr
                    category: sonarr
                instances:
                  - name: radarr
                    upstream: main
                    group: radarr
                  - name: radarr4k
                    upstream: main
                    group: radarr
                  - name: sonarr
                    upstream: main
                    group: sonarr
            """
        );

        var groups = DedupeGroups.Build(config);
        var radarr = Instance(config, "qbittorrent", "radarr");
        var radarr4k = Instance(config, "qbittorrent", "radarr4k");
        var sonarr = Instance(config, "qbittorrent", "sonarr");

        Assert.Same(groups.For(radarr), groups.For(radarr4k));
        Assert.NotSame(groups.For(radarr), groups.For(sonarr));
        Assert.Equal("qbittorrent|group:radarr", groups.For(radarr)!.Key);
        Assert.Equal("qbittorrent|group:sonarr", groups.For(sonarr)!.Key);
    }

    [Fact]
    public void Instance_names_can_be_reused_across_client_types()
    {
        var config = ConfigLoader.Parse(
            """
            clients:
              qbittorrent:
                upstreams:
                  - name: main
                    url: http://qbit:8080
                instances:
                  - name: radarr
                    upstream: main
              sabnzbd:
                upstreams:
                  - name: main
                    url: http://sab:8080
                instances:
                  - name: radarr
                    upstream: main
            """
        );

        Assert.Equal(2, config.ResolvedClients.Count);
        Assert.Equal("qbittorrent", config.ResolvedClients[0].Type);
        Assert.Equal("sabnzbd", config.ResolvedClients[1].Type);
    }

    [Theory]
    [InlineData("upstream", "missing")]
    [InlineData("group", "missing")]
    public void Unknown_instance_references_are_rejected(string field, string value)
    {
        var group = field == "group" ? $"\n        group: {value}" : "";
        var upstream = field == "upstream" ? value : "main";
        var ex = Assert.Throws<ConfigurationException>(() =>
            ConfigLoader.Parse(
                $"""
                clients:
                  qbittorrent:
                    upstreams:
                      - name: main
                        url: http://qbit:8080
                    instances:
                      - name: radarr
                        upstream: {upstream}{group}
                """
            )
        );

        Assert.Contains($"unknown {field}", ex.Message);
    }

    [Theory]
    [InlineData("upstreams", "url: http://one:8080", "url: http://two:8080")]
    [InlineData("groups", "category: one", "category: two")]
    [InlineData("instances", "upstream: main", "upstream: main")]
    public void Duplicate_names_within_a_section_are_rejected(
        string section,
        string firstValue,
        string secondValue
    )
    {
        var ex = Assert.Throws<ConfigurationException>(() =>
            ConfigLoader.Parse(
                $"""
                clients:
                  qbittorrent:
                    upstreams:
                      - name: main
                        url: http://qbit:8080
                    {section}:
                      - name: duplicate
                        {firstValue}
                      - name: duplicate
                        {secondValue}
                """
            )
        );

        Assert.Contains("Duplicate", ex.Message);
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
                  qbittorrent:
                    upstreams:
                      - name: main
                        url: http://localhost:8080
                    instances:
                      - name: "{name}"
                        upstream: main
                """
            )
        );

        Assert.Contains("invalid", ex.Message);
    }

    [Theory]
    [InlineData("localhost:8080")]
    [InlineData("ftp://localhost")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Invalid_upstream_urls_are_rejected(string url)
    {
        var ex = Assert.Throws<ConfigurationException>(() =>
            ConfigLoader.Parse(
                $"""
                clients:
                  qbittorrent:
                    upstreams:
                      - name: main
                        url: "{url}"
                """
            )
        );

        Assert.Contains("invalid URL", ex.Message);
    }

    [Fact]
    public void Announce_categories_on_a_qbittorrent_group_are_rejected()
    {
        var ex = Assert.Throws<ConfigurationException>(() =>
            ConfigLoader.Parse(
                """
                clients:
                  qbittorrent:
                    groups:
                      - name: main
                        announce_categories: [movies]
                """
            )
        );

        Assert.Contains("announce_categories", ex.Message);
    }

    [Fact]
    public void Names_that_match_root_endpoints_are_valid_inside_a_type_namespace()
    {
        var config = ConfigLoader.Parse(
            """
            clients:
              qbittorrent:
                upstreams:
                  - name: main
                    url: http://localhost:8080
                instances:
                  - name: healthz
                    upstream: main
            """
        );

        Assert.Equal("healthz", Assert.Single(config.ResolvedClients).Name);
    }

    [Fact]
    public void Applies_defaults_when_sections_are_omitted()
    {
        var config = ConfigLoader.Parse("clients: {}");

        Assert.Equal("0.0.0.0", config.Server.Host);
        Assert.Equal(8484, config.Server.Port);
        Assert.Equal("logfmt", config.Logging.Format);
        Assert.False(config.Logging.UsesJson);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, config.Logging.ParsedLevel);
        Assert.Empty(config.ResolvedClients);
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
            clients: {}
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
                clients:
                  sabznbd: {}
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

    private static ClientInstanceConfig Instance(ProxyConfig config, string type, string name) =>
        config.ResolvedClients.Single(instance => instance.Type == type && instance.Name == name);
}
