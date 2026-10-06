using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FluxMux.Updates.Core;

public sealed class AiFluxMuxReleaseResult
{
    public string StatusText { get; init; } = string.Empty;
    public bool NewerAvailable { get; init; }
    public string? DownloadUrl { get; init; }
    public string? ReleaseUrl { get; init; }
    public string? LatestVersion { get; init; }
}

public sealed class AiFluxMuxGithubAsset
{
    public required string Name { get; init; }
    public required string BrowserDownloadUrl { get; init; }
}

public sealed class AiFluxMuxGithubRelease
{
    public required string TagName { get; init; }
    public required string HtmlUrl { get; init; }
    public IReadOnlyList<AiFluxMuxGithubAsset> Assets { get; init; } = [];
}

/// <summary>
/// Checks the AI-FluxMux GitHub releases for a newer version, the same way the
/// llama-server updater checks ggml-org/llama.cpp. No user setup is required: the
/// repo is hardcoded and the download comes straight from the release assets.
/// </summary>
public static class AiFluxMuxReleaseCheck
{
    public const string GithubRepo = "ArchPrime/AI-FluxMux";

    public static async Task<AiFluxMuxReleaseResult> CheckAsync(
        HttpClient http,
        string currentVersion,
        string? sourceOverride = null,
        CancellationToken cancellationToken = default)
    {
        var repo = GithubUpdateSource.Normalize(sourceOverride) ?? GithubRepo;
        var releasesUrl = GithubUpdateSource.ReleasesUrl(repo, 30);

        string json;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, releasesUrl);
            request.Headers.UserAgent.ParseAdd(FluxMuxAppInfo.UserAgent);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            return new AiFluxMuxReleaseResult
            {
                StatusText = "Could not read AI-FluxMux releases from GitHub (" + repo + "): " + ex.Message
            };
        }

        var releases = ParseReleases(json);
        if (releases.Count == 0)
        {
            return new AiFluxMuxReleaseResult
            {
                StatusText = "No AI-FluxMux releases were listed on GitHub yet."
            };
        }

        // Pick the newest release whose version is newer than the running app.
        AiFluxMuxGithubRelease? best = null;
        foreach (var release in releases)
        {
            if (AppVersionComparer.Compare(currentVersion, release.TagName) >= 0)
            {
                continue;
            }

            if (best is null || AppVersionComparer.Compare(best.TagName, release.TagName) < 0)
            {
                best = release;
            }
        }

        var latest = releases[0];
        if (best is null)
        {
            return new AiFluxMuxReleaseResult
            {
                LatestVersion = latest.TagName,
                ReleaseUrl = latest.HtmlUrl,
                StatusText = "AI-FluxMux is up to date (latest release is " + latest.TagName + ")."
            };
        }

        var asset = PickDownloadAsset(best.Assets);
        if (asset is null)
        {
            return new AiFluxMuxReleaseResult
            {
                NewerAvailable = true,
                LatestVersion = best.TagName,
                ReleaseUrl = best.HtmlUrl,
                StatusText = "A newer AI-FluxMux (" + best.TagName + ") is on GitHub, but the release has no download file yet. Open the release page to download it."
            };
        }

        return new AiFluxMuxReleaseResult
        {
            NewerAvailable = true,
            LatestVersion = best.TagName,
            DownloadUrl = asset.BrowserDownloadUrl,
            ReleaseUrl = best.HtmlUrl,
            StatusText = "AI-FluxMux " + best.TagName + " is available on GitHub (you have " + currentVersion + ")."
        };
    }

    private static IReadOnlyList<AiFluxMuxGithubRelease> ParseReleases(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var releases = new List<AiFluxMuxGithubRelease>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var tag = item.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            var html = item.TryGetProperty("html_url", out var htmlEl) ? htmlEl.GetString() ?? string.Empty : string.Empty;
            var assets = new List<AiFluxMuxGithubAsset>();
            if (item.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsEl.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                    var url = asset.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() ?? string.Empty : string.Empty;
                    if (name.Length == 0 || url.Length == 0)
                    {
                        continue;
                    }

                    assets.Add(new AiFluxMuxGithubAsset { Name = name, BrowserDownloadUrl = url });
                }
            }

            releases.Add(new AiFluxMuxGithubRelease
            {
                TagName = tag,
                HtmlUrl = html,
                Assets = assets
            });
        }

        return releases;
    }

    private static AiFluxMuxGithubAsset? PickDownloadAsset(IReadOnlyList<AiFluxMuxGithubAsset> assets)
    {
        if (assets.Count == 0)
        {
            return null;
        }

        AiFluxMuxGithubAsset? Fallback(Func<AiFluxMuxGithubAsset, bool> predicate)
            => assets.FirstOrDefault(predicate);

        // Prefer a Windows installer/zip named for AI-FluxMux, then any exe/zip/msi, then the first asset.
        return Fallback(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && (a.Name.Contains("setup", StringComparison.OrdinalIgnoreCase)
                    || a.Name.Contains("installer", StringComparison.OrdinalIgnoreCase)
                    || a.Name.Contains("AI-FluxMux", StringComparison.OrdinalIgnoreCase)))
            ?? Fallback(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && a.Name.Contains("AI-FluxMux", StringComparison.OrdinalIgnoreCase))
            ?? Fallback(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            ?? Fallback(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            ?? Fallback(a => a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            ?? assets[0];
    }
}