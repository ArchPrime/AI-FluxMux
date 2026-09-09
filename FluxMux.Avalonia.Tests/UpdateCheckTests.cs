using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class UpdateCheckTests
{
    [Fact]
    public void Empty_feed_url_does_not_need_a_repository()
    {
        Assert.False(UpdateCheck.TryValidateFeedUrl("  ", out _, out var error));
        Assert.Contains("No feed URL yet", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Version_compare_treats_patch_as_newer()
    {
        Assert.True(AppVersionComparer.Compare("0.2", "0.2.1") < 0);
        Assert.Equal(0, AppVersionComparer.Compare("v0.2", "0.2"));
        Assert.Equal(0, AppVersionComparer.Compare("0.2 beta", "0.2"));
        Assert.True(AppVersionComparer.Compare("0.3", "0.2") > 0);
    }

    [Fact]
    public void Manifest_requires_fluxmux_protocol()
    {
        var feed = new Uri("https://example.com/fluxmux-updates.json");
        Assert.False(UpdateManifestParser.TryParse("""{"version":"0.3"}""", feed, out _, out var error));
        Assert.Contains("protocol", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Manifest_resolves_relative_help_url()
    {
        var feed = new Uri("https://example.com/updates/fluxmux-updates.json");
        Assert.True(UpdateManifestParser.TryParse(
            """{"protocol":"FluxMux","version":"0.2","helpUrl":"Help.html"}""",
            feed,
            out var manifest,
            out _));
        Assert.Equal("https://example.com/updates/Help.html", manifest!.HelpUrl);
    }

    [Fact]
    public async Task Replaces_help_when_feed_copy_differs()
    {
        var folder = Path.Combine(Path.GetTempPath(), "fluxmux-update-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(folder);
        var helpPath = Path.Combine(folder, "Help.html");
        await File.WriteAllTextAsync(helpPath, "<h2>Old</h2>");
        var newHelp = "<h2>New topic</h2><p>Updated.</p>";
        var feedJson = """
            {"protocol":"FluxMux","version":"0.2","helpUrl":"https://example.com/Help.html"}
            """;
        using var http = new HttpClient(new MapHandler(new Dictionary<string, string>
        {
            ["https://example.com/feed.json"] = feedJson,
            ["https://example.com/Help.html"] = newHelp
        }));

        try
        {
            var result = await UpdateCheck.RunAsync(http, "https://example.com/feed.json", "0.2", helpPath);
            Assert.True(result.HelpReplaced);
            Assert.False(result.AppUpdateAvailable);
            Assert.Equal(newHelp, await File.ReadAllTextAsync(helpPath));
            Assert.Contains("Help.html was updated", result.StatusText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Saves_help_to_the_per_user_copy_when_the_install_folder_refuses()
    {
        // Stands in for Program Files: AI-FluxMux cannot write beside its own exe, so the
        // update has to land in the per-user folder rather than report a failure.
        var folder = Path.Combine(Path.GetTempPath(), "fluxmux-update-" + Guid.NewGuid().ToString("n"));
        var readOnly = Path.Combine(folder, "install");
        var userFolder = Path.Combine(folder, "user");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(readOnly, "not a folder");
        var installedHelp = Path.Combine(readOnly, "Help.html");
        var userHelp = Path.Combine(userFolder, "Help.html");
        var newHelp = "<h2>New topic</h2><p>Updated.</p>";
        using var http = new HttpClient(new MapHandler(new Dictionary<string, string>
        {
            ["https://example.com/feed.json"] =
                """{"protocol":"FluxMux","version":"0.2","helpUrl":"https://example.com/Help.html"}""",
            ["https://example.com/Help.html"] = newHelp
        }));

        try
        {
            var result = await UpdateCheck.RunAsync(
                http,
                "https://example.com/feed.json",
                "0.2",
                installedHelp,
                userHelp);

            Assert.True(result.HelpReplaced);
            Assert.Equal(newHelp, await File.ReadAllTextAsync(userHelp));
            Assert.DoesNotContain("Could not save", result.StatusText, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(userFolder, "*.tmp"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Reports_the_failure_when_neither_copy_can_be_saved()
    {
        var folder = Path.Combine(Path.GetTempPath(), "fluxmux-update-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(folder);
        var blocked = Path.Combine(folder, "install");
        await File.WriteAllTextAsync(blocked, "not a folder");
        using var http = new HttpClient(new MapHandler(new Dictionary<string, string>
        {
            ["https://example.com/feed.json"] =
                """{"protocol":"FluxMux","version":"0.2","helpUrl":"https://example.com/Help.html"}""",
            ["https://example.com/Help.html"] = "<h2>New</h2>"
        }));

        try
        {
            var result = await UpdateCheck.RunAsync(
                http,
                "https://example.com/feed.json",
                "0.2",
                Path.Combine(blocked, "Help.html"),
                Path.Combine(blocked, "other", "Help.html"));

            Assert.False(result.HelpReplaced);
            Assert.Contains("Could not save Help.html", result.StatusText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Leaves_help_when_hash_matches()
    {
        var folder = Path.Combine(Path.GetTempPath(), "fluxmux-update-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(folder);
        var helpPath = Path.Combine(folder, "Help.html");
        var html = "<h2>Same</h2>";
        await File.WriteAllTextAsync(helpPath, html);
        using var http = new HttpClient(new MapHandler(new Dictionary<string, string>
        {
            ["https://example.com/feed.json"] = """{"protocol":"FluxMux","version":"0.2","helpUrl":"https://example.com/Help.html"}""",
            ["https://example.com/Help.html"] = html
        }));

        try
        {
            var result = await UpdateCheck.RunAsync(http, "https://example.com/feed.json", "0.2", helpPath);
            Assert.False(result.HelpReplaced);
            Assert.Contains("matches the feed", result.StatusText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Announces_newer_app_without_replacing_the_exe()
    {
        using var http = new HttpClient(new MapHandler(new Dictionary<string, string>
        {
            ["https://example.com/feed.json"] =
                """{"protocol":"FluxMux","version":"0.3","downloadUrl":"https://example.com/setup.exe","notes":"Bug fixes."}"""
        }));
        var result = await UpdateCheck.RunAsync(http, "https://example.com/feed.json", "0.2", Path.Combine(Path.GetTempPath(), "missing-Help.html"));
        Assert.True(result.AppUpdateAvailable);
        Assert.False(result.HelpReplaced);
        Assert.Equal("https://example.com/setup.exe", result.DownloadUrl);
        Assert.Contains("0.3", result.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejects_help_when_sha256_does_not_match()
    {
        var folder = Path.Combine(Path.GetTempPath(), "fluxmux-update-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(folder);
        var helpPath = Path.Combine(folder, "Help.html");
        await File.WriteAllTextAsync(helpPath, "<h2>Old</h2>");
        using var http = new HttpClient(new MapHandler(new Dictionary<string, string>
        {
            ["https://example.com/feed.json"] =
                """{"protocol":"FluxMux","version":"0.2","helpUrl":"https://example.com/Help.html","helpSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""",
            ["https://example.com/Help.html"] = "<h2>Tampered</h2>"
        }));

        try
        {
            var result = await UpdateCheck.RunAsync(http, "https://example.com/feed.json", "0.2", helpPath);
            Assert.False(result.HelpReplaced);
            Assert.Contains("helpSha256", result.StatusText, StringComparison.Ordinal);
            Assert.Equal("<h2>Old</h2>", await File.ReadAllTextAsync(helpPath));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private sealed class MapHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _map;

        public MapHandler(Dictionary<string, string> map)
        {
            _map = map;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (!_map.TryGetValue(url, out var body))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
