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
    /// <summary>Route prefixes that would collide with the proxy's own endpoints.</summary>
    private static readonly string[] ReservedNames = ["healthz"];

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

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var client in config.Clients)
        {
            if (client is null)
            {
                throw new ConfigurationException("clients contains an empty entry.");
            }

            if (!InstanceNameRegex().IsMatch(client.Name))
            {
                throw new ConfigurationException(
                    $"Client name '{client.Name}' is invalid. Names are used as URL prefixes and "
                        + "must be lowercase alphanumeric with optional inner hyphens (e.g. 'qbittorrent', 'sab-4k')."
                );
            }

            if (ReservedNames.Contains(client.Name))
            {
                throw new ConfigurationException($"Client name '{client.Name}' is reserved.");
            }

            if (!seenNames.Add(client.Name))
            {
                throw new ConfigurationException($"Duplicate client name '{client.Name}'.");
            }

            if (string.IsNullOrWhiteSpace(client.Type))
            {
                throw new ConfigurationException($"Client '{client.Name}' is missing a type.");
            }

            if (
                !Uri.TryCreate(client.Upstream, UriKind.Absolute, out var upstream)
                || (upstream.Scheme != Uri.UriSchemeHttp && upstream.Scheme != Uri.UriSchemeHttps)
            )
            {
                throw new ConfigurationException(
                    $"Client '{client.Name}' has invalid upstream '{client.Upstream}'. "
                        + "Expected an absolute http(s) URL, e.g. 'http://localhost:8080'."
                );
            }

            // A trailing slash would produce '//' when the endpoint path is appended.
            client.Upstream = client.Upstream.TrimEnd('/');
        }
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?$")]
    private static partial Regex InstanceNameRegex();
}
