using System;

namespace FluxMux.Updates.Core;

/// <summary>
/// Resolves the GitHub repository a component's update check reads from. All three
/// components (AI-FluxMux, llama-server, DeepSeek Harness) default to their upstream
/// GitHub repos. A user can nominate an alternate "owner/repo" to migrate the source
/// later; when blank, the default is used.
/// </summary>
public static class GithubUpdateSource
{
    /// <summary>Normalizes a user-supplied "owner/repo" (or full URL) to "owner/repo", or null.</summary>
    public static string? Normalize(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Accept a full URL like https://github.com/owner/repo (with or without .git / trailing slash).
        var marker = "github.com/";
        var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            text = text[(index + marker.Length)..];
        }

        text = text.Trim().TrimEnd('/');
        if (text.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^4];
        }

        // Must be exactly "owner/repo".
        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            return null;
        }

        return parts[0] + "/" + parts[1];
    }

    public static string ReleasesUrl(string ownerRepo, int perPage = 30)
        => "https://api.github.com/repos/" + ownerRepo + "/releases?per_page=" + perPage;
}