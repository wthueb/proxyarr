using System.Diagnostics;

namespace Proxyarr.IntegrationTests.Support;

/// <summary>
/// Decides once per test run whether the container-based tests can execute. Tests are skipped
/// (not failed) when Docker is unavailable, so 'dotnet test' stays green on machines without it.
/// </summary>
public static class DockerProbe
{
    private static readonly Lazy<string?> LazySkipReason = new(Probe);

    /// <summary>Null when integration tests can run, otherwise the reason to skip them.</summary>
    public static string? SkipReason => LazySkipReason.Value;

    private static string? Probe()
    {
        if (Environment.GetEnvironmentVariable("PROXYARR_SKIP_INTEGRATION") == "1")
        {
            return "PROXYARR_SKIP_INTEGRATION=1 is set.";
        }

        try
        {
            using var process = Process.Start(
                new ProcessStartInfo("docker", "info")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            );

            if (process is null)
            {
                return "Failed to start the 'docker' CLI.";
            }

            if (!process.WaitForExit(TimeSpan.FromSeconds(15)))
            {
                process.Kill();
                return "'docker info' timed out.";
            }

            return process.ExitCode == 0
                ? null
                : "'docker info' failed — is the Docker daemon running?";
        }
        catch (Exception ex)
        {
            return $"Docker CLI unavailable: {ex.Message}";
        }
    }
}
