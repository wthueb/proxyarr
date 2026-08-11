using System.Text;

namespace Proxyarr.Logging;

/// <summary>
/// Shared conventions for the logfmt and JSON formatters so both formats expose identical fields.
/// </summary>
public static class LogFields
{
    /// <summary>The structured-state key containing the fixed, unrendered message template.</summary>
    public const string OriginalFormatKey = "{OriginalFormat}";

    /// <summary>
    /// Returns the fixed message template carried by standard <see cref="ILogger"/> state. This
    /// keeps values out of <c>msg</c> even for framework and dependency logs that use templates.
    /// </summary>
    public static string? OriginalFormat(object? state)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> fields)
        {
            return null;
        }

        foreach (var (key, value) in fields)
        {
            if (key == OriginalFormatKey)
            {
                return value as string;
            }
        }

        return null;
    }

    /// <summary>
    /// Flattens ambient scopes and event-local state into output field names. Inner scopes replace
    /// outer scopes, and event-local values replace scoped values.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> CollectOutputFields(
        IExternalScopeProvider? scopeProvider,
        object? state
    )
    {
        var output = new Dictionary<string, object?>(StringComparer.Ordinal);
        scopeProvider?.ForEachScope(static (scope, fields) => MergeState(fields, scope), output);
        MergeState(output, state);
        return output;
    }

    private static void MergeState(Dictionary<string, object?> output, object? state)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> fields)
        {
            return;
        }

        foreach (var (key, value) in fields)
        {
            if (key != OriginalFormatKey)
            {
                output[NormalizeKey(key)] = value;
            }
        }
    }

    public static string LevelToken(LogLevel level) =>
        level switch
        {
            LogLevel.Trace => "trace",
            LogLevel.Debug => "debug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "error",
            LogLevel.Critical => "fatal",
            _ => "none",
        };

    /// <summary>Converts message-template placeholder names (PascalCase) to snake_case keys.</summary>
    public static string NormalizeKey(string key)
    {
        if (!key.Any(c => char.IsUpper(c) || c is ' ' or '.' or '-'))
        {
            return key;
        }

        var builder = new StringBuilder(key.Length + 4);
        for (var i = 0; i < key.Length; i++)
        {
            var current = key[i];
            if (current is ' ' or '.' or '-')
            {
                builder.Append('_');
                continue;
            }

            if (char.IsUpper(current))
            {
                var previous = i > 0 ? key[i - 1] : '\0';
                var next = i + 1 < key.Length ? key[i + 1] : '\0';
                var atWordBoundary =
                    previous is not ('\0' or '_' or ' ' or '.' or '-')
                    && (char.IsLower(previous) || char.IsDigit(previous) || char.IsLower(next));

                if (atWordBoundary)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(current));
            }
            else
            {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }
}
