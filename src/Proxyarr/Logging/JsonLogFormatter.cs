using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Proxyarr.Logging;

/// <summary>
/// Emits one JSON object per line with the same fields (and snake_case keys) as
/// <see cref="LogfmtFormatter"/>, so switching formats never changes field names.
/// </summary>
public sealed class JsonLogFormatter : ConsoleFormatter, IDisposable
{
    // Named distinctly from the framework's built-in "json" console formatter to avoid clashing
    // with its registration; the YAML value "json" maps to this formatter in Program.
    public const string FormatterName = "proxyarr-json";

    private readonly IDisposable? _optionsReload;
    private ConsoleFormatterOptions _options;

    public JsonLogFormatter(IOptionsMonitor<ConsoleFormatterOptions> options)
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

        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("ts", Timestamp());
            json.WriteString("level", LogFields.LevelToken(logEntry.LogLevel));
            json.WriteString("msg", message);
            json.WriteString("logger", logEntry.Category);

            if (_options.IncludeScopes && scopeProvider is not null)
            {
                scopeProvider.ForEachScope(
                    (scope, writer) => WriteStateFields(writer, scope),
                    json
                );
            }

            WriteStateFields(json, logEntry.State);

            if (logEntry.Exception is not null)
            {
                json.WriteString("exception", logEntry.Exception.ToString());
            }

            json.WriteEndObject();
        }

        textWriter.WriteLine(Encoding.UTF8.GetString(buffer.WrittenSpan));
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

    private static void WriteStateFields(Utf8JsonWriter json, object? state)
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

            var name = LogFields.NormalizeKey(key);
            switch (value)
            {
                case null:
                    json.WriteNull(name);
                    break;
                case bool boolean:
                    json.WriteBoolean(name, boolean);
                    break;
                case byte or sbyte or short or ushort or int or uint or long:
                    json.WriteNumber(name, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                    break;
                case ulong unsigned:
                    json.WriteNumber(name, unsigned);
                    break;
                case float single:
                    json.WriteNumber(name, single);
                    break;
                case double floating:
                    json.WriteNumber(name, floating);
                    break;
                case decimal number:
                    json.WriteNumber(name, number);
                    break;
                default:
                    json.WriteString(
                        name,
                        Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
                    );
                    break;
            }
        }
    }
}
