using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Proxyarr.Configuration;

/// <summary>
/// Loads and validates the YAML configuration file. YAML keys use snake_case; unknown keys are
/// rejected so typos fail fast at startup instead of being silently ignored.
/// </summary>
public static partial class ConfigLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    public static ProxyConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigurationException(
                $"Configuration file not found: '{Path.GetFullPath(path)}'. "
                    + "Pass --config <path> or set the PROXYARR_CONFIG environment variable."
            );
        }

        return Parse(File.ReadAllText(path), path);
    }

    public static ProxyConfig Parse(string yaml, string sourceName = "<inline>")
    {
        ProxyConfig config;
        try
        {
            config = Deserializer.Deserialize<ProxyConfig?>(yaml) ?? new ProxyConfig();
        }
        catch (YamlException ex)
        {
            throw new ConfigurationException(
                $"Failed to parse configuration '{sourceName}': {ex.Message}",
                ex
            );
        }

        Validate(config);
        return config;
    }

    private static void Validate(ProxyConfig config)
    {
        if (config.Server.Port is < 1 or > 65535)
        {
            throw new ConfigurationException(
                $"server.port must be between 1 and 65535, got {config.Server.Port}."
            );
        }

        if (string.IsNullOrWhiteSpace(config.Server.Host))
        {
            throw new ConfigurationException("server.host must not be empty.");
        }

        if (
            config.Logging.Format.ToLowerInvariant()
            is not (LoggingConfig.LogfmtFormat or LoggingConfig.JsonFormat)
        )
        {
            throw new ConfigurationException(
                $"Unknown log format '{config.Logging.Format}'. "
                    + $"Valid formats: {LoggingConfig.LogfmtFormat}, {LoggingConfig.JsonFormat}."
            );
        }

        // Force level parsing now so bad values fail at startup, not at first log write.
        _ = config.Logging.ParsedLevel;
        foreach (var _ in config.Logging.ParsedOverrides) { }

        if (config.Clients is null)
        {
            throw new ConfigurationException("clients must be a mapping.");
        }

        if (config.Clients.Qbittorrent is null || config.Clients.Sabnzbd is null)
        {
            throw new ConfigurationException(
                "clients.qbittorrent and clients.sabnzbd must be mappings when declared."
            );
        }

        config.ResolvedClients =
        [
            .. ResolveClientType("qbittorrent", config.Clients.Qbittorrent),
            .. ResolveClientType("sabnzbd", config.Clients.Sabnzbd),
        ];
    }

    private static IReadOnlyList<ClientInstanceConfig> ResolveClientType(
        string type,
        ClientTypeConfig clientType
    )
    {
        if (
            clientType.Upstreams is null
            || clientType.Groups is null
            || clientType.Instances is null
        )
        {
            throw new ConfigurationException(
                $"clients.{type} upstreams, groups, and instances must be lists when declared."
            );
        }

        var upstreams = new Dictionary<string, ClientUpstreamConfig>(
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var upstream in clientType.Upstreams)
        {
            if (upstream is null)
            {
                throw new ConfigurationException(
                    $"clients.{type}.upstreams contains an empty entry."
                );
            }

            ValidateName(type, "upstream", upstream.Name);
            if (!upstreams.TryAdd(upstream.Name, upstream))
            {
                throw new ConfigurationException(
                    $"Duplicate clients.{type} upstream name '{upstream.Name}'."
                );
            }

            if (
                !Uri.TryCreate(upstream.Url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            )
            {
                throw new ConfigurationException(
                    $"clients.{type} upstream '{upstream.Name}' has invalid URL "
                        + $"'{upstream.Url}'. Expected an absolute http(s) URL."
                );
            }

            upstream.Url = upstream.Url.TrimEnd('/');

            if (upstream.PathMappings is null)
            {
                throw new ConfigurationException(
                    $"clients.{type} upstream '{upstream.Name}' path_mappings must be a list."
                );
            }

            var seenFromPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mapping in upstream.PathMappings)
            {
                if (mapping is null)
                {
                    throw new ConfigurationException(
                        $"clients.{type} upstream '{upstream.Name}' path_mappings contains an empty entry."
                    );
                }

                mapping.From = ReportedPathMapper.NormalizeRoot(mapping.From);
                mapping.To = ReportedPathMapper.NormalizeRoot(mapping.To);
                if (!ReportedPathMapper.IsAbsolute(mapping.From))
                {
                    throw new ConfigurationException(
                        $"clients.{type} upstream '{upstream.Name}' path mapping 'from' must be an absolute path."
                    );
                }

                if (!ReportedPathMapper.IsAbsolute(mapping.To))
                {
                    throw new ConfigurationException(
                        $"clients.{type} upstream '{upstream.Name}' path mapping 'to' must be an absolute path."
                    );
                }

                if (!seenFromPaths.Add(ReportedPathMapper.ComparisonKey(mapping.From)))
                {
                    throw new ConfigurationException(
                        $"clients.{type} upstream '{upstream.Name}' has duplicate path mapping from '{mapping.From}'."
                    );
                }
            }

            upstream.PathMappings = upstream
                .PathMappings.OrderByDescending(mapping => mapping.From.Length)
                .ToList();
        }

        var groups = new Dictionary<string, ClientGroupConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in clientType.Groups)
        {
            if (group is null)
            {
                throw new ConfigurationException($"clients.{type}.groups contains an empty entry.");
            }

            ValidateName(type, "group", group.Name);
            if (!groups.TryAdd(group.Name, group))
            {
                throw new ConfigurationException(
                    $"Duplicate clients.{type} group name '{group.Name}'."
                );
            }

            if (group.AnnounceCategories is { Count: > 0 } && type != "sabnzbd")
            {
                throw new ConfigurationException(
                    $"clients.{type} group '{group.Name}': announce_categories is only "
                        + "valid for sabnzbd groups."
                );
            }
        }

        var seenInstances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<ClientInstanceConfig>(clientType.Instances.Count);
        foreach (var instance in clientType.Instances)
        {
            if (instance is null)
            {
                throw new ConfigurationException(
                    $"clients.{type}.instances contains an empty entry."
                );
            }

            ValidateName(type, "instance", instance.Name);
            if (!seenInstances.Add(instance.Name))
            {
                throw new ConfigurationException(
                    $"Duplicate clients.{type} instance name '{instance.Name}'."
                );
            }

            if (!upstreams.TryGetValue(instance.Upstream, out var upstream))
            {
                throw new ConfigurationException(
                    $"clients.{type} instance '{instance.Name}' references unknown "
                        + $"upstream '{instance.Upstream}'."
                );
            }

            ClientGroupConfig? group = null;
            if (instance.Group is not null && !groups.TryGetValue(instance.Group, out group))
            {
                throw new ConfigurationException(
                    $"clients.{type} instance '{instance.Name}' references unknown group "
                        + $"'{instance.Group}'."
                );
            }

            resolved.Add(
                new ClientInstanceConfig
                {
                    Name = instance.Name,
                    Type = type,
                    Upstream = upstream.Url,
                    PathMappings = upstream.PathMappings,
                    Dedupe = group is null
                        ? null
                        : new DedupeConfig
                        {
                            Group = group.Name,
                            Category = group.Category,
                            AnnounceCategories = group.AnnounceCategories,
                        },
                }
            );
        }

        return resolved;
    }

    private static void ValidateName(string type, string kind, string name)
    {
        if (InstanceNameRegex().IsMatch(name))
        {
            return;
        }

        throw new ConfigurationException(
            $"clients.{type} {kind} name '{name}' is invalid. Names must be lowercase "
                + "alphanumeric with optional inner hyphens (e.g. 'main', 'radarr-4k')."
        );
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?$")]
    private static partial Regex InstanceNameRegex();
}
