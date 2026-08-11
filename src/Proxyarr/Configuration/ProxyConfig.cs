namespace Proxyarr.Configuration;

/// <summary>Root of the YAML configuration file.</summary>
public sealed class ProxyConfig
{
    public ServerConfig Server { get; set; } = new();

    public LoggingConfig Logging { get; set; } = new();

    public List<ClientInstanceConfig> Clients { get; set; } = [];

    /// <summary>
    /// Path to the SQLite database that backs SABnzbd cross-instance dedup. Optional; defaults to
    /// <c>proxyarr.db</c> next to the config file (set in <c>Program</c>). Only used when at least
    /// one sabnzbd client has dedupe enabled.
    /// </summary>
    public string? Database { get; set; }
}

public sealed class LoggingConfig
{
    public const string LogfmtFormat = "logfmt";
    public const string JsonFormat = "json";

    /// <summary>Minimum level: trace, debug, information, warning, error, critical, or none.</summary>
    public string Level { get; set; } = "information";

    /// <summary>Output format: logfmt (default) or json.</summary>
    public string Format { get; set; } = LogfmtFormat;

    /// <summary>
    /// Per-category level overrides, e.g. <c>"Microsoft.AspNetCore": debug</c> to re-enable
    /// framework logs that the proxy dials down to warning by default.
    /// </summary>
    public Dictionary<string, string> Overrides { get; set; } = [];

    public bool UsesJson => Format.Equals(JsonFormat, StringComparison.OrdinalIgnoreCase);

    public LogLevel ParsedLevel => ParseLevel(Level);

    public IEnumerable<KeyValuePair<string, LogLevel>> ParsedOverrides =>
        Overrides.Select(entry => new KeyValuePair<string, LogLevel>(
            entry.Key,
            ParseLevel(entry.Value)
        ));

    public static LogLevel ParseLevel(string value) =>
        value.ToLowerInvariant() switch
        {
            "trace" => LogLevel.Trace,
            "debug" => LogLevel.Debug,
            "info" or "information" => LogLevel.Information,
            "warn" or "warning" => LogLevel.Warning,
            "error" => LogLevel.Error,
            "fatal" or "critical" => LogLevel.Critical,
            "none" or "off" => LogLevel.None,
            _ => throw new ConfigurationException(
                $"Unknown log level '{value}'. "
                    + "Valid levels: trace, debug, information, warning, error, critical, none."
            ),
        };
}

public sealed class ServerConfig
{
    public string Host { get; set; } = "0.0.0.0";

    public int Port { get; set; } = 8484;
}

/// <summary>
/// One proxied download client instance. Requests to <c>/{name}/...</c> are forwarded to
/// <see cref="Upstream"/>, restricted to the endpoints declared by the adapter for <see cref="Type"/>.
/// </summary>
public sealed class ClientInstanceConfig
{
    /// <summary>Route prefix for this instance. In Radarr, set the client's "URL Base" to <c>/{name}</c>.</summary>
    public string Name { get; set; } = "";

    /// <summary>Adapter type, e.g. <c>qbittorrent</c> or <c>sabnzbd</c>.</summary>
    public string Type { get; set; } = "";

    /// <summary>
    /// Base URL of the real download client, including any URL base it is served under
    /// (e.g. <c>http://sab-host:8080/sabnzbd</c>).
    /// </summary>
    public string Upstream { get; set; } = "";

    /// <summary>
    /// Opt-in cross-instance download deduplication. Null (the common case) means byte-identical
    /// pass-through. See <see cref="DedupeConfig"/>.
    /// </summary>
    public DedupeConfig? Dedupe { get; set; }

    /// <summary>Whether this instance participates in cross-instance dedup.</summary>
    public bool DedupeEnabled => Dedupe is { Enabled: true };
}

/// <summary>
/// Per-instance cross-instance deduplication settings. Instances of the same client type that share
/// a normalized upstream URL (or an explicit <see cref="Group"/>) form a dedup group: a release
/// grabbed by several of them is downloaded once and shared, tracked by the proxy instance name.
/// </summary>
public sealed class DedupeConfig
{
    /// <summary>Master switch. When false, the instance is a plain pass-through and the other keys must be unset.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The real qBittorrent/SABnzbd category assigned to shared downloads. When unset, content is
    /// added with no category at all (the category the *arr sends is never forwarded upstream).
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// Optional override that forces this instance into a named group. Groups are normally derived
    /// automatically from the upstream URL; this is only for exotic setups where one client is
    /// reachable via two hostnames.
    /// </summary>
    public string? Group { get; set; }

    /// <summary>
    /// SABnzbd only: category names to inject into the <c>get_config</c> response so Radarr's
    /// "category exists" check passes without those categories existing upstream.
    /// </summary>
    public List<string>? AnnounceCategories { get; set; }
}
