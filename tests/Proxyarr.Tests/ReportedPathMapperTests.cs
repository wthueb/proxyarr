using Proxyarr.Configuration;

namespace Proxyarr.Tests;

public class ReportedPathMapperTests
{
    [Fact]
    public void Rewrites_a_unix_path_on_a_segment_boundary()
    {
        var mappings = Mappings(("/downloads", "/proxyarr/qbit-a"));

        Assert.Equal(
            "/proxyarr/qbit-a/Movie/file.mkv",
            ReportedPathMapper.Rewrite("/downloads/Movie/file.mkv", mappings)
        );
        Assert.Equal(
            "/downloads-other/Movie/file.mkv",
            ReportedPathMapper.Rewrite("/downloads-other/Movie/file.mkv", mappings)
        );
    }

    [Fact]
    public void Uses_the_longest_matching_prefix()
    {
        var mappings = Mappings(
            ("/downloads/special", "/proxyarr/special"),
            ("/downloads", "/proxyarr/default")
        );

        Assert.Equal(
            "/proxyarr/special/Movie",
            ReportedPathMapper.Rewrite("/downloads/special/Movie", mappings)
        );
    }

    [Fact]
    public void Rewrites_windows_paths_case_insensitively_and_converts_separators()
    {
        var mappings = Mappings((@"C:\Downloads", "/proxyarr/qbit-windows"));

        Assert.Equal(
            "/proxyarr/qbit-windows/Movie/file.mkv",
            ReportedPathMapper.Rewrite(@"c:/downloads/Movie\file.mkv", mappings)
        );
    }

    [Fact]
    public void Can_report_a_unix_upstream_path_as_a_windows_path()
    {
        var mappings = Mappings(("/downloads", @"R:\Proxyarr\Sab"));

        Assert.Equal(
            @"R:\Proxyarr\Sab\Movie\file.mkv",
            ReportedPathMapper.Rewrite("/downloads/Movie/file.mkv", mappings)
        );
    }

    [Fact]
    public void Preserves_forward_slashes_in_a_windows_target_that_uses_them()
    {
        var mappings = Mappings(("/downloads", "R:/Proxyarr/Sab"));

        Assert.Equal(
            "R:/Proxyarr/Sab/Movie/file.mkv",
            ReportedPathMapper.Rewrite("/downloads/Movie/file.mkv", mappings)
        );
    }

    private static IReadOnlyList<ClientPathMappingConfig> Mappings(
        params (string From, string To)[] values
    ) =>
        values
            .Select(value => new ClientPathMappingConfig
            {
                From = ReportedPathMapper.NormalizeRoot(value.From),
                To = ReportedPathMapper.NormalizeRoot(value.To),
            })
            .OrderByDescending(mapping => mapping.From.Length)
            .ToList();
}
