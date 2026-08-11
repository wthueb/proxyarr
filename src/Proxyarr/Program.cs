using Microsoft.Extensions.Logging.Console;
using Proxyarr.Clients;
using Proxyarr.Configuration;
using Proxyarr.Logging;

var builder = WebApplication.CreateBuilder(args);

var configPath =
    builder.Configuration["config"]
    ?? Environment.GetEnvironmentVariable("PROXYARR_CONFIG")
    ?? "config.yml";

var config = ConfigLoader.Load(configPath);

// Default the SQLite path (SABnzbd dedup only) to sit next to the config file, so a relative
// working directory can't move it. Program owns this default so the DI registration sees a
// concrete path.
config.Database ??= Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".",
    "proxyarr.db"
);

builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(config.Logging.ParsedLevel);
builder.Logging.AddConsole(options =>
    options.FormatterName = config.Logging.UsesJson
        ? JsonLogFormatter.FormatterName
        : LogfmtFormatter.FormatterName
);
builder.Logging.AddConsoleFormatter<LogfmtFormatter, ConsoleFormatterOptions>(ConfigureFormatter);
builder.Logging.AddConsoleFormatter<JsonLogFormatter, ConsoleFormatterOptions>(ConfigureFormatter);

// Framework categories are dialed down so per-request detail comes from the proxy's own logs;
// logging.overrides can re-raise any of these. Yarp is off entirely because its forwarder logs
// full upstream URLs including query-string credentials — the proxy's own logs carry the same
// information with those values redacted.
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);
builder.Logging.AddFilter("Yarp", LogLevel.None);
foreach (var (category, level) in config.Logging.ParsedOverrides)
{
    builder.Logging.AddFilter(category, level);
}

builder.WebHost.UseUrls($"http://{config.Server.Host}:{config.Server.Port}");
builder.Services.AddSingleton(config);
builder.Services.AddDownloadClients();

var app = builder.Build();

app.Logger.LogInformation(
    "Loaded configuration from {ConfigPath} ({ClientCount} clients, {LogFormat} logs at {LogLevel})",
    Path.GetFullPath(configPath),
    config.Clients.Count,
    config.Logging.UsesJson ? "json" : "logfmt",
    LogFields.LevelToken(config.Logging.ParsedLevel)
);

app.MapGet("/healthz", () => Results.Text("OK"));
app.MapDownloadClients();

app.Run();

void ConfigureFormatter(ConsoleFormatterOptions options)
{
    options.UseUtcTimestamp = true;
    options.IncludeScopes = config.Logging.IncludeScopes;
}
