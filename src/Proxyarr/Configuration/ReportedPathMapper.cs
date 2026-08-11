namespace Proxyarr.Configuration;

/// <summary>
/// Replaces a download client's path prefix with a synthetic prefix reported through Proxyarr.
/// Matching is path-segment aware, uses the longest configured prefix, and understands both Unix
/// and Windows separators regardless of the OS Proxyarr itself runs on.
/// </summary>
public static class ReportedPathMapper
{
    public static string Rewrite(string path, IReadOnlyList<ClientPathMappingConfig> mappings)
    {
        foreach (var mapping in mappings)
        {
            if (!HasPrefix(path, mapping.From))
            {
                continue;
            }

            var suffix = path[mapping.From.Length..].TrimStart('/', '\\');
            if (suffix.Length == 0)
            {
                return mapping.To;
            }

            var separator = PreferredSeparator(mapping.To);
            suffix = suffix.Replace('/', separator).Replace('\\', separator);
            return mapping.To.EndsWith('/') || mapping.To.EndsWith('\\')
                ? mapping.To + suffix
                : mapping.To + separator + suffix;
        }

        return path;
    }

    public static bool IsAbsolute(string path) =>
        path.StartsWith('/')
        || path.StartsWith("\\\\", StringComparison.Ordinal)
        || (
            path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == ':'
            && IsSeparator(path[2])
        );

    public static string NormalizeRoot(string path)
    {
        path = path.Trim();
        var minimumLength =
            path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == ':'
            && IsSeparator(path[2])
                ? 3
            : path.StartsWith("\\\\", StringComparison.Ordinal) ? 2
            : 1;

        while (path.Length > minimumLength && IsSeparator(path[^1]))
        {
            path = path[..^1];
        }

        return path;
    }

    public static string ComparisonKey(string path)
    {
        var normalized = NormalizeRoot(path).Replace('\\', '/');
        return IsWindowsPath(path) ? normalized.ToUpperInvariant() : normalized;
    }

    private static bool HasPrefix(string path, string prefix)
    {
        if (path.Length < prefix.Length)
        {
            return false;
        }

        var ignoreCase = IsWindowsPath(prefix);
        for (var index = 0; index < prefix.Length; index++)
        {
            var expected = prefix[index];
            var actual = path[index];
            if (IsSeparator(expected) && IsSeparator(actual))
            {
                continue;
            }

            if (
                ignoreCase
                    ? char.ToUpperInvariant(expected) != char.ToUpperInvariant(actual)
                    : expected != actual
            )
            {
                return false;
            }
        }

        return path.Length == prefix.Length
            || IsSeparator(prefix[^1])
            || IsSeparator(path[prefix.Length]);
    }

    private static bool IsWindowsPath(string path) =>
        path.StartsWith("\\\\", StringComparison.Ordinal)
        || (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':');

    private static char PreferredSeparator(string path) =>
        path.StartsWith("\\\\", StringComparison.Ordinal) ? '\\'
        : path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' ? path[2]
        : '/';

    private static bool IsSeparator(char value) => value is '/' or '\\';
}
