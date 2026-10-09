using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace FluxMux.Updates.Core;

public sealed class LlamaCppGithubAsset
{
    public required string Name { get; init; }
    public required string BrowserDownloadUrl { get; init; }
}

public sealed class LlamaCppGithubRelease
{
    public required string TagName { get; init; }
    public required string HtmlUrl { get; init; }
    public IReadOnlyList<LlamaCppGithubAsset> Assets { get; init; } = [];
}

public sealed class LlamaServerUpdateMatch
{
    public int RemoteBuild { get; init; }
    public string ReleaseUrl { get; init; } = string.Empty;
    public string? ServerZipName { get; init; }
    public string? ServerZipUrl { get; init; }
    public string? CudartZipName { get; init; }
    public string? CudartZipUrl { get; init; }
    public bool MultipleCudaMinors { get; init; }
    /// <summary>The distinct CUDA-minor labels available in the release (e.g. "12.4", "12.6").</summary>
    public IReadOnlyList<string> CudaMinors { get; init; } = [];
}

public static class LlamaCppReleaseMatcher
{
    public static IReadOnlyList<LlamaCppGithubRelease> ParseReleases(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var releases = new List<LlamaCppGithubRelease>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var tag = item.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? string.Empty : string.Empty;
            if (LlamaCppBuildNumber.TryParse(tag) is null)
            {
                continue;
            }

            var html = item.TryGetProperty("html_url", out var htmlEl) ? htmlEl.GetString() ?? string.Empty : string.Empty;
            var assets = new List<LlamaCppGithubAsset>();
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

                    assets.Add(new LlamaCppGithubAsset { Name = name, BrowserDownloadUrl = url });
                }
            }

            releases.Add(new LlamaCppGithubRelease
            {
                TagName = tag,
                HtmlUrl = html,
                Assets = assets
            });
        }

        return releases;
    }

    public static LlamaServerUpdateMatch? FindNewerMatching(
        IReadOnlyList<LlamaCppGithubRelease> releases,
        LlamaServerFamily family,
        int? installedBuild,
        string? preferredCudaMinor = null)
    {
        if (!family.CanMatch)
        {
            return null;
        }

        LlamaCppGithubRelease? best = null;
        var bestBuild = 0;
        foreach (var release in releases)
        {
            var build = LlamaCppBuildNumber.TryParse(release.TagName);
            if (build is null)
            {
                continue;
            }

            if (!release.Assets.Any(asset =>
                    LlamaServerFamilyFingerprint.IsLlamaServerZip(asset.Name)
                    && LlamaServerFamilyFingerprint.AssetMatchesFamily(asset.Name, family)))
            {
                continue;
            }

            if (best is null || build.Value > bestBuild)
            {
                best = release;
                bestBuild = build.Value;
            }
        }

        if (best is null)
        {
            return null;
        }

        if (installedBuild is not null && bestBuild <= installedBuild.Value)
        {
            return new LlamaServerUpdateMatch
            {
                RemoteBuild = bestBuild,
                ReleaseUrl = best.HtmlUrl
            };
        }

        var serverZips = best.Assets
            .Where(asset =>
                LlamaServerFamilyFingerprint.IsLlamaServerZip(asset.Name)
                && LlamaServerFamilyFingerprint.AssetMatchesFamily(asset.Name, family))
            .ToList();
        var cudartZips = best.Assets
            .Where(asset =>
                LlamaServerFamilyFingerprint.IsCudartZip(asset.Name)
                && LlamaServerFamilyFingerprint.AssetMatchesFamily(asset.Name, family))
            .ToList();

        var cudaMinors = serverZips
            .Select(asset => ExtractCudaMinor(asset.Name))
            .Where(minor => minor is not null)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var ambiguous = family.Backend == "cuda" && family.CudaExact is null && cudaMinors.Count > 1;

        // If the CUDA minor is ambiguous but the caller has a remembered preference, use it
        // to pick the matching zip. This makes the "Update now" button work without manual
        // selection when the user has previously chosen a minor.
        string? resolvedServerZipName = null;
        string? resolvedServerZipUrl = null;
        string? resolvedCudartZipName = null;
        string? resolvedCudartZipUrl = null;
        if (ambiguous && !string.IsNullOrWhiteSpace(preferredCudaMinor))
        {
            var preferred = serverZips.FirstOrDefault(asset =>
                string.Equals(ExtractCudaMinor(asset.Name), preferredCudaMinor, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
            {
                resolvedServerZipName = preferred.Name;
                resolvedServerZipUrl = preferred.BrowserDownloadUrl;
                var preferredCudart = cudartZips.FirstOrDefault(asset =>
                    string.Equals(ExtractCudaMinor(asset.Name), preferredCudaMinor, StringComparison.OrdinalIgnoreCase));
                resolvedCudartZipName = preferredCudart?.Name;
                resolvedCudartZipUrl = preferredCudart?.BrowserDownloadUrl;
                ambiguous = false; // resolved
            }
        }

        return new LlamaServerUpdateMatch
        {
            RemoteBuild = bestBuild,
            ReleaseUrl = best.HtmlUrl,
            ServerZipName = ambiguous ? null : (resolvedServerZipName ?? serverZips.FirstOrDefault()?.Name),
            ServerZipUrl = ambiguous ? null : (resolvedServerZipUrl ?? serverZips.FirstOrDefault()?.BrowserDownloadUrl),
            CudartZipName = ambiguous ? null : (resolvedCudartZipName ?? cudartZips.FirstOrDefault()?.Name),
            CudartZipUrl = ambiguous ? null : (resolvedCudartZipUrl ?? cudartZips.FirstOrDefault()?.BrowserDownloadUrl),
            MultipleCudaMinors = ambiguous,
            CudaMinors = cudaMinors
        };
    }

    private static string? ExtractCudaMinor(string name)
    {
        var marker = "cuda-";
        var index = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var start = index + marker.Length;
        var end = start;
        while (end < name.Length && (char.IsDigit(name[end]) || name[end] == '.'))
        {
            end++;
        }

        var token = name[start..end].Trim('.');
        return token.Length > 0 ? token : null;
    }
}
