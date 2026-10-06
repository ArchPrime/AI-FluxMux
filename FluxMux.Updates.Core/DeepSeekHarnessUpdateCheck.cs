using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FluxMux.Updates.Core;

public sealed class DeepSeekHarnessUpdateResult
{
    public string StatusText { get; init; } = string.Empty;
    public string? InstalledVersion { get; init; }
    public string LaunchMode { get; init; } = string.Empty;
    public bool NpmCheckSucceeded { get; init; }
    public string? LatestVersion { get; init; }
    public bool NewerAvailable { get; init; }
}

public static class DeepSeekHarnessUpdateCheck
{
    public const string NpmRegistryUrl = "https://registry.npmjs.org/@deepseek-ai%2Fdsh";
    public const string ReleasesUrl = "https://github.com/deepseek-ai/deepseek-harness/releases";
    public const string DefaultGithubRepo = "deepseek-ai/deepseek-harness";

    /// <summary>
    /// Resolves the DeepSeek Harness launcher to run. Kept out of the shared core so the
    /// app can supply its own resolver (which knows about custom dsh paths) while the
    /// standalone updater falls back to a PATH lookup.
    /// </summary>
    public delegate (string FileName, string ArgumentPrefix, string Mode) HarnessLauncherResolver(string? dshExecutablePath);

    private static HarnessLauncherResolver _launcherResolver = PathBasedLauncherResolver;

    /// <summary>
    /// The app sets this to its own resolver (e.g. one that honours a configured dsh path).
    /// </summary>
    public static void SetLauncherResolver(HarnessLauncherResolver resolver)
    {
        _launcherResolver = resolver ?? PathBasedLauncherResolver;
    }

    /// <summary>
    /// Minimal PATH-based fallback used by the standalone updater when the app has not
    /// registered a resolver. Mirrors the app's behaviour: a real dsh on PATH, else npx.
    /// </summary>
    public static (string FileName, string ArgumentPrefix, string Mode) PathBasedLauncherResolver(string? dshExecutablePath)
    {
        if (!string.IsNullOrWhiteSpace(dshExecutablePath))
        {
            var trimmed = dshExecutablePath.Trim().Trim('"');
            if (File.Exists(trimmed))
            {
                return (trimmed, string.Empty, "dsh (custom path)");
            }
        }

        foreach (var name in new[] { "dsh", "dsh.exe", "dsh.cmd" })
        {
            var found = FindOnPath(name);
            if (!string.IsNullOrWhiteSpace(found))
            {
                return (found, string.Empty, "dsh");
            }
        }

        var npxCmd = FindOnPath("npx.cmd");
        if (!string.IsNullOrWhiteSpace(npxCmd))
        {
            return (npxCmd, "@deepseek-ai/dsh", "npx @deepseek-ai/dsh");
        }

        var npx = FindOnPath("npx");
        if (!string.IsNullOrWhiteSpace(npx))
        {
            return (npx, "@deepseek-ai/dsh", "npx @deepseek-ai/dsh");
        }

        return ("dsh", string.Empty, "dsh");
    }

    /// <summary>
    /// True when Node.js (npm or npx) is available on PATH. The Harness update runs
    /// "npm install -g @deepseek-ai/dsh", so Node.js is a hard prerequisite. The app's
    /// "Update now" button calls this before closing the app, so the user is told up
    /// front (with a link to the installer) if Node.js is missing.
    /// </summary>
    public static bool IsNodeAvailable()
    {
        return !string.IsNullOrWhiteSpace(FindOnPath("npm.cmd"))
            || !string.IsNullOrWhiteSpace(FindOnPath("npm"))
            || !string.IsNullOrWhiteSpace(FindOnPath("npx.cmd"))
            || !string.IsNullOrWhiteSpace(FindOnPath("npx"));
    }

    public static DeepSeekHarnessUpdateResult ReadInstalled()
    {
        var (fileName, argumentPrefix, mode) = _launcherResolver(null);
        var raw = ReadVersionText(fileName, argumentPrefix);
        return new DeepSeekHarnessUpdateResult
        {
            InstalledVersion = DeepSeekHarnessVersion.TryParse(raw),
            LaunchMode = mode,
            StatusText = DeepSeekHarnessVersion.FormatStatus(
                DeepSeekHarnessVersion.TryParse(raw),
                mode,
                npmLatest: null,
                npmNext: null)
        };
    }

    public static async Task<DeepSeekHarnessUpdateResult> CheckAsync(
        HttpClient http,
        CancellationToken cancellationToken = default)
    {
        var installed = ReadInstalled();
        string? latest = null;
        string? next = null;
        var npmOk = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, NpmRegistryUrl);
            request.Headers.UserAgent.ParseAdd(FluxMuxAppInfo.UserAgent);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            (latest, next) = DeepSeekHarnessVersion.ReadNpmDistTags(json);
            npmOk = !string.IsNullOrWhiteSpace(latest) || !string.IsNullOrWhiteSpace(next);
        }
        catch (Exception ex)
        {
            return new DeepSeekHarnessUpdateResult
            {
                InstalledVersion = installed.InstalledVersion,
                LaunchMode = installed.LaunchMode,
                StatusText = installed.StatusText + " Could not read npm for @deepseek-ai/dsh: " + ex.Message
            };
        }

        return new DeepSeekHarnessUpdateResult
        {
            InstalledVersion = installed.InstalledVersion,
            LaunchMode = installed.LaunchMode,
            NpmCheckSucceeded = npmOk,
            StatusText = DeepSeekHarnessVersion.FormatStatus(
                installed.InstalledVersion,
                installed.LaunchMode,
                latest,
                next)
        };
    }

    /// <summary>
    /// Checks the DeepSeek Harness version against the latest GitHub release tag, the
    /// same way AI-FluxMux and llama-server check their GitHub repos. This is the default
    /// "is there something newer?" source. The actual update still goes through npm (the
    /// Harness is an npm package; its GitHub releases publish changelogs only, no binary).
    /// </summary>
    public static async Task<DeepSeekHarnessUpdateResult> CheckGithubAsync(
        HttpClient http,
        string? sourceOverride = null,
        CancellationToken cancellationToken = default)
    {
        var installed = ReadInstalled();
        var repo = GithubUpdateSource.Normalize(sourceOverride) ?? DefaultGithubRepo;
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
            return new DeepSeekHarnessUpdateResult
            {
                InstalledVersion = installed.InstalledVersion,
                LaunchMode = installed.LaunchMode,
                StatusText = installed.StatusText + " Could not read DeepSeek Harness releases from GitHub (" + repo + "): " + ex.Message
            };
        }

        var latestTag = LatestReleaseVersion(json);
        if (string.IsNullOrWhiteSpace(latestTag))
        {
            return new DeepSeekHarnessUpdateResult
            {
                InstalledVersion = installed.InstalledVersion,
                LaunchMode = installed.LaunchMode,
                StatusText = installed.StatusText + " No DeepSeek Harness releases were listed on GitHub yet."
            };
        }

        var installedVersion = installed.InstalledVersion;
        var newer = !string.IsNullOrWhiteSpace(installedVersion)
            && AppVersionComparer.Compare(installedVersion, latestTag) < 0;

        var status = string.IsNullOrWhiteSpace(installedVersion)
            ? installed.StatusText + " Latest GitHub release is " + latestTag + "."
            : newer
                ? "This PC's DeepSeek Harness is " + installedVersion + "; GitHub's latest release is " + latestTag + ". A newer version is available."
                : "This PC's DeepSeek Harness is " + installedVersion + "; that matches GitHub's latest release (" + latestTag + ").";

        return new DeepSeekHarnessUpdateResult
        {
            InstalledVersion = installedVersion,
            LaunchMode = installed.LaunchMode,
            LatestVersion = latestTag,
            NewerAvailable = newer,
            StatusText = status
        };
    }

    /// <summary>
    /// Returns the version of the newest GitHub release, parsed from its tag
    /// (e.g. "dsh-v0.2.1-alpha.1" -> "0.2.1-alpha.1"). Null when none can be read.
    /// </summary>
    public static string? LatestReleaseVersion(string releasesJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(releasesJson);
        if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return null;
        }

        string? best = null;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var tag = item.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? string.Empty : string.Empty;
            var version = DeepSeekHarnessVersion.TryParse(tag);
            if (string.IsNullOrWhiteSpace(version))
            {
                continue;
            }

            if (best is null || AppVersionComparer.Compare(best, version) < 0)
            {
                best = version;
            }
        }

        return best;
    }

    private static string? FindOnPath(string fileName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : new[] { string.Empty };

        foreach (var folder in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(folder.Trim(), fileName);
                if (!candidate.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(extension))
                {
                    candidate += extension;
                }

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    internal static string ReadVersionText(string fileName, string argumentPrefix)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var arguments = string.IsNullOrWhiteSpace(argumentPrefix)
            ? "--version"
            : argumentPrefix.Trim() + " --version";
        if (argumentPrefix.Contains("@deepseek-ai/dsh", StringComparison.OrdinalIgnoreCase)
            && !arguments.Contains("--yes", StringComparison.OrdinalIgnoreCase))
        {
            arguments = "--yes " + arguments;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(8000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return string.Empty;
            }

            return (stdout + " " + stderr).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }
}
