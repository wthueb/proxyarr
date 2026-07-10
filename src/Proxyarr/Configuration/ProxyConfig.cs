namespace Proxyarr.Configuration;

/// <summary>Root of the YAML configuration file.</summary>
public sealed class ProxyConfig
{
    public ServerConfig Server { get; set; } = new();

    public LoggingConfig Logging { get; set; } = new();

    public List<ClientInstanceConfig> Clients { get; set; } = [];
}

public sealed class LoggingConfig
{
    public const string LogfmtFormat = "logfmt";
    public const string JsonFormat = "json";

    /// <summary>Minimum level: trace, debug, information, warning, error, critical, or none.</summary>
    public string Level { get; set; } = "information";

    /// <summary>Output format: logfmt (default) or json.</summary>
    public string Format { get; set; } = LogfmtFormat;

    /// <summary>Emit logger scopes (e.g. ASP.NET Core's request scope fields) as extra fields.</summary>
    public bool IncludeScopes { get; set; }

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
}
