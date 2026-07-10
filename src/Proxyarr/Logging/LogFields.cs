using System.Text;

namespace Proxyarr.Logging;

/// <summary>
/// Shared conventions for the logfmt and JSON formatters so both formats expose identical fields.
/// </summary>
public static class LogFields
{
    /// <summary>The message-template placeholder key that duplicates the formatted message.</summary>
    public const string OriginalFormatKey = "{OriginalFormat}";

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
