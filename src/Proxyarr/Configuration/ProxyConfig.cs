using YamlDotNet.Serialization;

namespace Proxyarr.Configuration;

/// <summary>Root of the YAML configuration file.</summary>
public sealed class ProxyConfig
{
    public ServerConfig Server { get; set; } = new();

    public LoggingConfig Logging { get; set; } = new();

    public ClientsConfig Clients { get; set; } = new();

    /// <summary>Validated runtime instances resolved from the named client configuration.</summary>
    [YamlIgnore]
    public IReadOnlyList<ClientInstanceConfig> ResolvedClients { get; internal set; } = [];

    /// <summary>
    /// Path to the SQLite database that backs SABnzbd cross-instance dedup. Optional; defaults to
    /// <c>proxyarr.db</c> next to the config file (set in <c>Program</c>). Only used when at least
    /// one sabnzbd instance references a group.
    /// </summary>
    public string? Database { get; set; }
}

/// <summary>Download-client configuration grouped by adapter type.</summary>
public sealed class ClientsConfig
{
    public ClientTypeConfig Qbittorrent { get; set; } = new();

    public ClientTypeConfig Sabnzbd { get; set; } = new();
}

/// <summary>Named upstreams, dedupe groups, and routed instances for one client type.</summary>
public sealed class ClientTypeConfig
{
    public List<ClientUpstreamConfig> Upstreams { get; set; } = [];

    public List<ClientGroupConfig> Groups { get; set; } = [];

    public List<ClientInstanceSpec> Instances { get; set; } = [];
}

public sealed class ClientUpstreamConfig
{
    public string Name { get; set; } = "";

    public string Url { get; set; } = "";
}

public sealed class ClientGroupConfig
{
    public string Name { get; set; } = "";

    /// <summary>The real category assigned to downloads shared by this group.</summary>
    public string? Category { get; set; }

    /// <summary>SABnzbd category names to inject into its <c>get_config</c> response.</summary>
    public List<string>? AnnounceCategories { get; set; }
}

public sealed class ClientInstanceSpec
{
    public string Name { get; set; } = "";

    /// <summary>Name of an entry in the client type's <c>upstreams</c> list.</summary>
    public string Upstream { get; set; } = "";

    /// <summary>
    /// Optional name of an entry in the client type's <c>groups</c> list. Setting this implicitly
    /// enables dedupe; omitting it makes the instance a pass-through.
    /// </summary>
    public string? Group { get; set; }
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
/// One proxied download client instance. Requests to <c>/{type}/{name}/...</c> are forwarded to
/// <see cref="Upstream"/>, restricted to the endpoints declared by the adapter for <see cref="Type"/>.
/// </summary>
public sealed class ClientInstanceConfig
{
    /// <summary>
    /// Name within the client type's route namespace. In Radarr, set the client's "URL Base" to
    /// <c>/{type}/{name}</c>.
    /// </summary>
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
    public bool DedupeEnabled => Dedupe is not null;
}

/// <summary>
/// Resolved per-instance cross-instance deduplication settings. Instances that reference the same
/// named group form a dedupe group: a release grabbed by several of them is downloaded once and
/// shared, tracked by the proxy instance name.
/// </summary>
public sealed class DedupeConfig
{
    /// <summary>
    /// The real qBittorrent/SABnzbd category assigned to shared downloads. When unset, content is
    /// added with no category at all (the category the *arr sends is never forwarded upstream).
    /// </summary>
    public string? Category { get; set; }

    /// <summary>The configured group name.</summary>
    public string Group { get; set; } = "";

    /// <summary>
    /// SABnzbd only: category names to inject into the <c>get_config</c> response so Radarr's
    /// "category exists" check passes without those categories existing upstream.
    /// </summary>
    public List<string>? AnnounceCategories { get; set; }
}
