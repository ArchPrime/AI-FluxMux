using System;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FluxMux.Updates.Core;

public static class DeepSeekHarnessVersion
{
    private static readonly Regex VersionRegex = new(
        @"\d+\.\d+\.\d+(?:-[A-Za-z0-9.]+)?",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = VersionRegex.Match(text.Trim());
        return match.Success ? match.Value : null;
    }

    public static (string? Latest, string? Next) ReadNpmDistTags(string? registryJson)
    {
        if (string.IsNullOrWhiteSpace(registryJson))
        {
            return (null, null);
        }

        try
        {
            var root = JsonNode.Parse(registryJson) as JsonObject;
            var tags = root?["dist-tags"] as JsonObject;
            return (tags?["latest"]?.ToString(), tags?["next"]?.ToString());
        }
        catch
        {
            return (null, null);
        }
    }

    public static string FormatStatus(
        string? installed,
        string launchMode,
        string? npmLatest,
        string? npmNext)
    {
        var haveInstalled = !string.IsNullOrWhiteSpace(installed);
        var mode = string.IsNullOrWhiteSpace(launchMode) ? "DeepSeek Harness" : launchMode.Trim();
        var installedLine = haveInstalled
            ? "This PC's DeepSeek Harness is " + installed + " (" + mode + ")."
            : "DeepSeek Harness is not reporting a version yet. Install dsh (or Node.js so npx @deepseek-ai/dsh works), then Check for updates again.";

        if (string.IsNullOrWhiteSpace(npmLatest) && string.IsNullOrWhiteSpace(npmNext))
        {
            return installedLine;
        }

        var tags = " npm latest is "
            + (string.IsNullOrWhiteSpace(npmLatest) ? "not listed" : npmLatest)
            + "; npm next is "
            + (string.IsNullOrWhiteSpace(npmNext) ? "not listed" : npmNext)
            + ".";
        if (haveInstalled
            && !string.IsNullOrWhiteSpace(npmLatest)
            && installed!.Equals(npmLatest, StringComparison.OrdinalIgnoreCase))
        {
            return installedLine + tags + " That matches npm latest.";
        }

        return installedLine
            + tags
            + " AI-FluxMux can update DeepSeek Harness via the 'Update now' button (which runs the standalone updater to back up your config and update dsh via npm). If you prefer to update manually, back up %USERPROFILE%\\.dsh first; preview builds can change on-disk settings.";
    }
}
