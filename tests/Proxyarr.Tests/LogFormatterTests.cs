using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Proxyarr.Logging;
using Proxyarr.Tests.Support;

namespace Proxyarr.Tests;

public class LogfmtFormatterTests
{
    private static string Format(
        LogLevel level = LogLevel.Information,
        string category = "Test.Category",
        string message = "hello",
        IReadOnlyList<KeyValuePair<string, object?>>? state = null,
        Exception? exception = null,
        IExternalScopeProvider? scopeProvider = null
    )
    {
        using var formatter = new LogfmtFormatter(
            new StaticOptionsMonitor<ConsoleFormatterOptions>(new() { UseUtcTimestamp = true })
        );
        using var writer = new StringWriter();
        var entry = new LogEntry<IReadOnlyList<KeyValuePair<string, object?>>>(
            level,
            category,
            new EventId(0),
            state ?? [],
            exception,
            (_, _) => message
        );
        formatter.Write(in entry, scopeProvider, writer);
        return writer.ToString();
    }

    [Fact]
    public void Emits_core_fields_in_logfmt()
    {
        var line = Format(
            message: "Proxied qbit GET /qbit/api/v2/torrents/info -> 200 in 3.1ms",
            state:
            [
                new("Instance", "qbit"),
                new("StatusCode", 200),
                new("ElapsedMs", 3.1),
                new("{OriginalFormat}", "Proxied {Instance} ... {StatusCode} ... {ElapsedMs}"),
            ]
        );

        Assert.StartsWith("ts=", line);
        Assert.Contains(" level=info ", line);
        Assert.Contains("msg=\"Proxied {Instance} ... {StatusCode} ... {ElapsedMs}\"", line);
        Assert.Contains(" logger=Test.Category ", line);
        Assert.Contains(" instance=qbit ", line);
        Assert.Contains(" status_code=200 ", line);
        Assert.Contains(" elapsed_ms=3.1", line);
        Assert.DoesNotContain("OriginalFormat", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Quotes_and_escapes_values()
    {
        var line = Format(message: "he said \"hi\"\nnext", state: [new("Plain", "no-quotes")]);

        Assert.Contains("msg=\"he said \\\"hi\\\"\\nnext\"", line);
        Assert.Contains(" plain=no-quotes", line);
    }

    [Fact]
    public void Quotes_empty_values()
    {
        var line = Format(state: [new("Empty", "")]);

        Assert.Contains(" empty=\"\"", line);
    }

    [Fact]
    public void Includes_exception_details()
    {
        var line = Format(level: LogLevel.Error, exception: new InvalidOperationException("boom"));

        Assert.Contains(" level=error ", line);
        Assert.Contains("exception=\"System.InvalidOperationException: boom", line);
    }

    [Fact]
    public void Scoped_fields_are_emitted_once_and_popped_on_dispose()
    {
        var scopes = new LoggerExternalScopeProvider();
        string scopedLine;
        using (
            scopes.Push(
                new Dictionary<string, object?> { ["Instance"] = "qbit", ["Shared"] = "scope" }
            )
        )
        {
            scopedLine = Format(state: [new("Shared", "event")], scopeProvider: scopes);
        }

        var unscopedLine = Format(scopeProvider: scopes);

        Assert.Contains(" instance=qbit ", scopedLine);
        Assert.Contains(" shared=event", scopedLine);
        Assert.Equal(1, scopedLine.Split(" shared=", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain(" instance=", unscopedLine);
    }

    [Theory]
    [InlineData(LogLevel.Trace, "trace")]
    [InlineData(LogLevel.Debug, "debug")]
    [InlineData(LogLevel.Information, "info")]
    [InlineData(LogLevel.Warning, "warn")]
    [InlineData(LogLevel.Error, "error")]
    [InlineData(LogLevel.Critical, "fatal")]
    public void Maps_levels_to_logfmt_tokens(LogLevel level, string token)
    {
        Assert.Contains($" level={token} ", Format(level: level));
    }
}

public class JsonLogFormatterTests
{
    private static JsonDocument Format(
        string message = "hello",
        IReadOnlyList<KeyValuePair<string, object?>>? state = null,
        Exception? exception = null,
        IExternalScopeProvider? scopeProvider = null
    )
    {
        using var formatter = new JsonLogFormatter(
            new StaticOptionsMonitor<ConsoleFormatterOptions>(new() { UseUtcTimestamp = true })
        );
        using var writer = new StringWriter();
        var entry = new LogEntry<IReadOnlyList<KeyValuePair<string, object?>>>(
            LogLevel.Warning,
            "Test.Category",
            new EventId(0),
            state ?? [],
            exception,
            (_, _) => message
        );
        formatter.Write(in entry, scopeProvider, writer);
        return JsonDocument.Parse(writer.ToString());
    }

    [Fact]
    public void Emits_the_same_fields_as_logfmt_as_json()
    {
        using var log = Format(
            message: "something happened",
            state:
            [
                new("Instance", "sab"),
                new("StatusCode", 502),
                new("ElapsedMs", 15.4),
                new("{OriginalFormat}", "Something happened"),
            ]
        );

        var root = log.RootElement;
        Assert.True(DateTimeOffset.TryParse(root.GetProperty("ts").GetString(), out _));
        Assert.Equal("warn", root.GetProperty("level").GetString());
        Assert.Equal("Something happened", root.GetProperty("msg").GetString());
        Assert.Equal("Test.Category", root.GetProperty("logger").GetString());
        Assert.Equal("sab", root.GetProperty("instance").GetString());
        Assert.Equal(502, root.GetProperty("status_code").GetInt32());
        Assert.Equal(15.4, root.GetProperty("elapsed_ms").GetDouble());
        Assert.False(root.TryGetProperty("original_format", out _));
    }

    [Fact]
    public void Includes_exception_details()
    {
        using var log = Format(exception: new InvalidOperationException("boom"));

        Assert.Contains(
            "System.InvalidOperationException: boom",
            log.RootElement.GetProperty("exception").GetString()
        );
    }

    [Fact]
    public void Scoped_fields_preserve_types_and_event_values_take_precedence()
    {
        var scopes = new LoggerExternalScopeProvider();
        using var scope = scopes.Push(
            new Dictionary<string, object?> { ["Instance"] = "sab", ["Attempt"] = 1 }
        );
        using var log = Format(state: [new("Attempt", 2)], scopeProvider: scopes);

        Assert.Equal("sab", log.RootElement.GetProperty("instance").GetString());
        Assert.Equal(2, log.RootElement.GetProperty("attempt").GetInt32());
        Assert.Single(log.RootElement.EnumerateObject(), property => property.Name == "attempt");
    }
}

public class LogFieldsTests
{
    [Fact]
    public void Reads_the_static_message_from_structured_state()
    {
        var state = new Dictionary<string, object?>
        {
            ["Path"] = "/private/path",
            [LogFields.OriginalFormatKey] = "Request received at {Path}",
        };

        Assert.Equal("Request received at {Path}", LogFields.OriginalFormat(state));
    }

    [Theory]
    [InlineData("StatusCode", "status_code")]
    [InlineData("ElapsedMs", "elapsed_ms")]
    [InlineData("HTTPMethod", "http_method")]
    [InlineData("EventID", "event_id")]
    [InlineData("Path", "path")]
    [InlineData("already_snake", "already_snake")]
    [InlineData("Some Key", "some_key")]
    [InlineData("Dotted.Name", "dotted_name")]
    public void Normalizes_keys_to_snake_case(string input, string expected)
    {
        Assert.Equal(expected, LogFields.NormalizeKey(input));
    }
}
