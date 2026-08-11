using System.Collections;

namespace Proxyarr.Logging;

/// <summary>
/// Writes a fixed event description as the log message while keeping event-specific values in
/// structured fields. Unlike message-template logging, field values are never rendered into
/// <c>msg</c>.
/// </summary>
public static class StructuredLoggingExtensions
{
    public static void LogTrace(
        this ILogger logger,
        string message,
        (string Name, object? Value) firstField,
        params (string Name, object? Value)[] fields
    ) => Log(logger, LogLevel.Trace, exception: null, message, firstField, fields);

    public static void LogDebug(
        this ILogger logger,
        string message,
        (string Name, object? Value) firstField,
        params (string Name, object? Value)[] fields
    ) => Log(logger, LogLevel.Debug, exception: null, message, firstField, fields);

    public static void LogInformation(
        this ILogger logger,
        string message,
        (string Name, object? Value) firstField,
        params (string Name, object? Value)[] fields
    ) => Log(logger, LogLevel.Information, exception: null, message, firstField, fields);

    public static void LogWarning(
        this ILogger logger,
        string message,
        (string Name, object? Value) firstField,
        params (string Name, object? Value)[] fields
    ) => Log(logger, LogLevel.Warning, exception: null, message, firstField, fields);

    public static void LogError(
        this ILogger logger,
        Exception? exception,
        string message,
        (string Name, object? Value) firstField,
        params (string Name, object? Value)[] fields
    ) => Log(logger, LogLevel.Error, exception, message, firstField, fields);

    public static void LogCritical(
        this ILogger logger,
        Exception? exception,
        string message,
        (string Name, object? Value) firstField,
        params (string Name, object? Value)[] fields
    ) => Log(logger, LogLevel.Critical, exception, message, firstField, fields);

    private static void Log(
        ILogger logger,
        LogLevel level,
        Exception? exception,
        string message,
        (string Name, object? Value) firstField,
        (string Name, object? Value)[] fields
    )
    {
        if (!logger.IsEnabled(level))
        {
            return;
        }

        var state = new StructuredLogState(message, firstField, fields);
        logger.Log(
            level,
            eventId: default,
            state,
            exception,
            static (logState, _) => logState.Message
        );
    }

    private sealed class StructuredLogState(
        string message,
        (string Name, object? Value) firstField,
        (string Name, object? Value)[] fields
    ) : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public string Message { get; } = message;

        public int Count => fields.Length + 2;

        public KeyValuePair<string, object?> this[int index] =>
            index == fields.Length + 1 ? new(LogFields.OriginalFormatKey, Message)
            : index == 0 ? new(firstField.Name, firstField.Value)
            : new(fields[index - 1].Name, fields[index - 1].Value);

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            yield return new(firstField.Name, firstField.Value);

            foreach (var (name, value) in fields)
            {
                yield return new(name, value);
            }

            yield return new(LogFields.OriginalFormatKey, Message);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => Message;
    }
}
