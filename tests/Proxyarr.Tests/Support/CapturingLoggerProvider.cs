using Microsoft.Extensions.Logging;

namespace Proxyarr.Tests.Support;

/// <summary>Collects log events, including their structured state, for assertions.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public sealed record LogEvent(
        string Category,
        LogLevel Level,
        string Message,
        IReadOnlyDictionary<string, object?> Fields
    );

    private readonly List<LogEvent> _events = [];

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

    private void Add(LogEvent logEvent)
    {
        lock (_events)
        {
            _events.Add(logEvent);
        }
    }

    private sealed class CapturingLogger(CapturingLoggerProvider provider, string category)
        : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            var fields = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(pair => pair.Key, pair => pair.Value)
                : [];
            provider.Add(new LogEvent(category, logLevel, formatter(state, exception), fields));
        }
    }
}
