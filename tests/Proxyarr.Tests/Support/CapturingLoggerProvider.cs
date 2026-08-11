using Microsoft.Extensions.Logging;

namespace Proxyarr.Tests.Support;

/// <summary>Collects log events, including their structured state, for assertions.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    public sealed record LogEvent(
        string Category,
        LogLevel Level,
        string Message,
        IReadOnlyDictionary<string, object?> Fields
    );

    private readonly List<LogEvent> _events = [];
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    public IReadOnlyList<LogEvent> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void Dispose() { }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) =>
        _scopeProvider = scopeProvider;

    private void Add(LogEvent logEvent)
    {
        lock (_events)
        {
            _events.Add(logEvent);
        }
    }

    private IDisposable PushScope<TState>(TState state)
        where TState : notnull => _scopeProvider.Push(state);

    private IReadOnlyDictionary<string, object?> CollectFields(object? state)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        _scopeProvider.ForEachScope(static (scope, values) => Merge(values, scope), fields);
        Merge(fields, state);
        return fields;
    }

    private static void Merge(Dictionary<string, object?> fields, object? state)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            return;
        }

        foreach (var (key, value) in pairs)
        {
            fields[key] = value;
        }
    }

    private sealed class CapturingLogger(CapturingLoggerProvider provider, string category)
        : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => provider.PushScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            var fields = provider.CollectFields(state);
            provider.Add(new LogEvent(category, logLevel, formatter(state, exception), fields));
        }
    }
}
