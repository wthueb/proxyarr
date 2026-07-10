using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Proxyarr.Logging;

/// <summary>
/// Emits one logfmt line per event:
/// <c>ts=2026-07-09T12:00:00.000Z level=info msg="Proxied ..." logger=... instance=qbit status_code=200</c>.
/// Structured state values (message template arguments) become snake_case key=value pairs.
/// </summary>
public sealed class LogfmtFormatter : ConsoleFormatter, IDisposable
{
    public const string FormatterName = "logfmt";

    private readonly IDisposable? _optionsReload;
    private ConsoleFormatterOptions _options;

    public LogfmtFormatter(IOptionsMonitor<ConsoleFormatterOptions> options)
        : base(FormatterName)
    {
        _options = options.CurrentValue;
        _optionsReload = options.OnChange(updated => _options = updated);
    }

    public void Dispose() => _optionsReload?.Dispose();

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter
    )
    {
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message) && logEntry.Exception is null)
        {
            return;
        }

        textWriter.Write("ts=");
        textWriter.Write(Timestamp());
        textWriter.Write(" level=");
        textWriter.Write(LogFields.LevelToken(logEntry.LogLevel));
        WritePair(textWriter, "msg", message);
        WritePair(textWriter, "logger", logEntry.Category);

        if (_options.IncludeScopes && scopeProvider is not null)
        {
            scopeProvider.ForEachScope(
                (scope, writer) => WriteStateFields(writer, scope),
                textWriter
            );
        }

        WriteStateFields(textWriter, logEntry.State);

        if (logEntry.Exception is not null)
        {
            WritePair(textWriter, "exception", logEntry.Exception.ToString());
        }

        textWriter.Write(Environment.NewLine);
    }

    private string Timestamp()
    {
        var now = _options.UseUtcTimestamp ? DateTimeOffset.UtcNow : DateTimeOffset.Now;
        var format =
            _options.TimestampFormat
            ?? (
                _options.UseUtcTimestamp
                    ? "yyyy-MM-dd'T'HH:mm:ss.fff'Z'"
                    : "yyyy-MM-dd'T'HH:mm:ss.fffzzz"
            );
        return now.ToString(format, CultureInfo.InvariantCulture);
    }

    private static void WriteStateFields(TextWriter writer, object? state)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> fields)
        {
            return;
        }

        foreach (var (key, value) in fields)
        {
            if (key == LogFields.OriginalFormatKey)
            {
                continue;
            }

            WritePair(
                writer,
                LogFields.NormalizeKey(key),
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
            );
        }
    }

    private static void WritePair(TextWriter writer, string key, string value)
    {
        writer.Write(' ');
        writer.Write(key);
        writer.Write('=');
        WriteValue(writer, value);
    }

    private static void WriteValue(TextWriter writer, string value)
    {
        if (value.Length > 0 && !NeedsQuoting(value))
        {
            writer.Write(value);
            return;
        }

        writer.Write('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    writer.Write("\\\"");
                    break;
                case '\\':
                    writer.Write("\\\\");
                    break;
                case '\n':
                    writer.Write("\\n");
                    break;
                case '\r':
                    writer.Write("\\r");
                    break;
                case '\t':
                    writer.Write("\\t");
                    break;
                default:
                    writer.Write(c);
                    break;
            }
        }

        writer.Write('"');
    }

    private static bool NeedsQuoting(string value) =>
        value.Any(c => c is ' ' or '"' or '=' or '\\' || char.IsControl(c));
}
