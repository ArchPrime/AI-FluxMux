using FluxMux.Updates.Core;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class DeepSeekHarnessVersionTests
{
    [Theory]
    [InlineData("0.1.1-rc.2", "0.1.1-rc.2")]
    [InlineData("dsh 0.1.0-rc.7", "0.1.0-rc.7")]
    [InlineData("version: 1.2.3", "1.2.3")]
    public void Parses_npm_style_versions(string raw, string expected)
    {
        Assert.Equal(expected, DeepSeekHarnessVersion.TryParse(raw));
    }

    [Fact]
    public void Missing_text_is_not_a_version()
    {
        Assert.Null(DeepSeekHarnessVersion.TryParse("dsh: command not found"));
    }

    [Fact]
    public void Reads_latest_and_next_dist_tags()
    {
        var (latest, next) = DeepSeekHarnessVersion.ReadNpmDistTags(
            """{"dist-tags":{"latest":"0.1.1-rc.2","next":"0.1.2-alpha.1"}}""");
        Assert.Equal("0.1.1-rc.2", latest);
        Assert.Equal("0.1.2-alpha.1", next);
    }

    [Fact]
    public void Matching_latest_does_not_tell_the_operator_to_upgrade()
    {
        var text = DeepSeekHarnessVersion.FormatStatus(
            "0.1.1-rc.2",
            "dsh",
            "0.1.1-rc.2",
            "0.1.1-rc.2");
        Assert.Contains("This PC's DeepSeek Harness is 0.1.1-rc.2", text);
        Assert.Contains("matches npm latest", text);
        Assert.DoesNotContain("update dsh yourself", text);
    }

    [Fact]
    public void Differing_latest_says_fluxmux_does_not_install_harness()
    {
        var text = DeepSeekHarnessVersion.FormatStatus(
            "0.1.0-rc.7",
            "npx @deepseek-ai/dsh",
            "0.1.1-rc.2",
            "0.1.1-rc.2");
        Assert.Contains("AI-FluxMux can update DeepSeek Harness", text);
        Assert.Contains(".dsh", text);
    }

    [Fact]
    public void Latest_release_version_picks_rc_over_alpha_with_same_numeric_triple()
    {
        // Simulates the GitHub releases list (newest-first) that broke the harness update.
        // The comparator must pick 0.2.1-rc.2 (the newest under semver), not 0.2.1-alpha.5.
        var releasesJson = """
            [
              {"tag_name":"dsh-v0.2.1-rc.2"},
              {"tag_name":"dsh-v0.2.1-rc.1"},
              {"tag_name":"dsh-v0.2.1-alpha.5"},
              {"tag_name":"dsh-v0.2.1-alpha.4"},
              {"tag_name":"dsh-v0.2.0-rc.2"},
              {"tag_name":"dsh-v0.1.7-rc.2"}
            ]
            """;
        Assert.Equal("0.2.1-rc.2", DeepSeekHarnessUpdateCheck.LatestReleaseVersion(releasesJson));
    }

    [Fact]
    public void Latest_release_version_picks_stable_over_prerelease()
    {
        // A stable release beats any prerelease with the same numeric triple.
        var releasesJson = """
            [
              {"tag_name":"dsh-v0.2.1"},
              {"tag_name":"dsh-v0.2.1-rc.3"},
              {"tag_name":"dsh-v0.2.1-alpha.5"}
            ]
            """;
        Assert.Equal("0.2.1", DeepSeekHarnessUpdateCheck.LatestReleaseVersion(releasesJson));
    }

    [Fact]
    public void Latest_release_version_picks_highest_rc_when_no_stable()
    {
        // When no stable exists, the highest rc wins over lower rc and any alpha.
        var releasesJson = """
            [
              {"tag_name":"dsh-v0.2.1-rc.3"},
              {"tag_name":"dsh-v0.2.1-rc.2"},
              {"tag_name":"dsh-v0.2.1-alpha.5"}
            ]
            """;
        Assert.Equal("0.2.1-rc.3", DeepSeekHarnessUpdateCheck.LatestReleaseVersion(releasesJson));
    }
}
