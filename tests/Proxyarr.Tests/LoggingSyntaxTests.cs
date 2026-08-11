using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Proxyarr.Tests;

public sealed class LoggingSyntaxTests
{
    private static readonly HashSet<string> LogMethods =
    [
        "LogTrace",
        "LogDebug",
        "LogInformation",
        "LogWarning",
        "LogError",
        "LogCritical",
    ];

    private static readonly HashSet<string> AmbientRequestFields =
    [
        "Instance",
        "Method",
        "Path",
        "Query",
    ];

    [Fact]
    public void Production_log_calls_use_static_messages_and_structured_fields()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "src", "Proxyarr");
        var failures = new List<string>();
        var checkedCalls = 0;
        var cancellationToken = TestContext.Current.CancellationToken;

        foreach (var path in ProductionSourceFiles(sourceRoot))
        {
            var tree = CSharpSyntaxTree.ParseText(
                File.ReadAllText(path),
                CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview),
                path,
                cancellationToken: cancellationToken
            );
            failures.AddRange(
                tree.GetDiagnostics(cancellationToken)
                    .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                    .Select(diagnostic => diagnostic.ToString())
            );

            foreach (
                var invocation in tree.GetRoot(cancellationToken)
                    .DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
            )
            {
                if (
                    invocation.Expression is not MemberAccessExpressionSyntax member
                    || !LogMethods.Contains(member.Name.Identifier.ValueText)
                )
                {
                    continue;
                }

                checkedCalls++;
                Validate(invocation, repositoryRoot, path, failures);
            }
        }

        Assert.True(checkedCalls > 0, $"No logging calls found under {sourceRoot}");
        Assert.True(
            failures.Count == 0,
            $"Invalid production logging calls:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}"
        );
    }

    private static void Validate(
        InvocationExpressionSyntax invocation,
        string repositoryRoot,
        string path,
        List<string> failures
    )
    {
        var arguments = invocation.ArgumentList.Arguments;
        var messageIndex =
            arguments.Count > 0
            && arguments[0].Expression.IsKind(SyntaxKind.StringLiteralExpression)
                ? 0
                : 1;
        var location = invocation.GetLocation().GetLineSpan().StartLinePosition;
        var displayPath = Path.GetRelativePath(repositoryRoot, path);
        var prefix = $"{displayPath}:{location.Line + 1}";

        if (
            arguments.Count <= messageIndex
            || arguments[messageIndex].Expression is not LiteralExpressionSyntax message
            || !message.IsKind(SyntaxKind.StringLiteralExpression)
        )
        {
            failures.Add($"{prefix}: msg must be a string literal");
            return;
        }

        if (message.Token.ValueText.IndexOfAny(['{', '}']) >= 0)
        {
            failures.Add($"{prefix}: msg must not contain template placeholders");
        }

        for (var index = messageIndex + 1; index < arguments.Count; index++)
        {
            if (
                arguments[index].Expression is not TupleExpressionSyntax tuple
                || tuple.Arguments.Count != 2
                || tuple.Arguments[0].Expression is not LiteralExpressionSyntax fieldName
                || !fieldName.IsKind(SyntaxKind.StringLiteralExpression)
                || string.IsNullOrWhiteSpace(fieldName.Token.ValueText)
            )
            {
                failures.Add(
                    $"{prefix}: argument {index + 1} must be a (\"FieldName\", value) tuple"
                );
                continue;
            }

            if (AmbientRequestFields.Contains(fieldName.Token.ValueText))
            {
                failures.Add(
                    $"{prefix}: {fieldName.Token.ValueText} belongs in the ambient request scope"
                );
            }
        }
    }

    private static IEnumerable<string> ProductionSourceFiles(string sourceRoot) =>
        Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
                !path.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj")
            );

    private static string FindRepositoryRoot()
    {
        for (
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent
        )
        {
            if (
                File.Exists(Path.Combine(directory.FullName, "Proxyarr.slnx"))
                && Directory.Exists(Path.Combine(directory.FullName, "src", "Proxyarr"))
            )
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not find the Proxyarr repository above {AppContext.BaseDirectory}"
        );
    }
}
