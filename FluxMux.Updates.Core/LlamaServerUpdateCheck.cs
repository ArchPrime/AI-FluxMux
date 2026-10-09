using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FluxMux.Updates.Core;

public sealed class LlamaServerUpdateResult
{
    public string StatusText { get; init; } = string.Empty;
    public string? ServerZipUrl { get; init; }
    public string? CudartZipUrl { get; init; }
    public string? ReleaseUrl { get; init; }
    public bool NewerAvailable { get; init; }
    public string? InstalledBuildLabel { get; init; }
    /// <summary>
    /// When the release ships multiple CUDA-minor zips and the installed family's minor is
    /// ambiguous, this lists the available CUDA-minor labels (e.g. "12.4", "12.6", "13.0").
    /// The UI can show a picker; the chosen minor is remembered in config so future checks
    /// are unambiguous.
    /// </summary>
    public IReadOnlyList<string> CudaMinors { get; init; } = [];
    /// <summary>True when the CUDA minor is ambiguous and a picker is needed.</summary>
    public bool CudaMinorAmbiguous { get; init; }
}

public static class LlamaServerUpdateCheck
{
    public const string DefaultGithubRepo = "ggml-org/llama.cpp";

    public const string GithubReleasesUrl = "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=20";

    public static string ReadVersionText(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return string.Empty;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(4000))
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

    public static async Task<LlamaServerUpdateResult> CheckAsync(
        HttpClient http,
        string? executablePath,
        string? sourceOverride = null,
        CancellationToken cancellationToken = default,
        string? preferredCudaMinor = null)
    {
        var family = LlamaServerFamilyFingerprint.FromInstallDirectory(executablePath);
        if (!family.CanMatch)
        {
            return new LlamaServerUpdateResult { StatusText = family.Summary };
        }

        var versionText = ReadVersionText(executablePath ?? string.Empty);
        var installedBuild = LlamaCppBuildNumber.TryParse(versionText);
        var repo = GithubUpdateSource.Normalize(sourceOverride) ?? DefaultGithubRepo;
        var releasesUrl = GithubUpdateSource.ReleasesUrl(repo, 20);
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
            return new LlamaServerUpdateResult
            {
                StatusText = family.Summary + " Could not read llama.cpp releases (" + repo + "): " + ex.Message
            };
        }

        var releases = LlamaCppReleaseMatcher.ParseReleases(json);
        var match = LlamaCppReleaseMatcher.FindNewerMatching(releases, family, installedBuild, preferredCudaMinor);
        var installedLabel = installedBuild is null ? "unknown build" : "b" + installedBuild.Value;
        if (match is null)
        {
            return new LlamaServerUpdateResult
            {
                InstalledBuildLabel = installedBuild is null ? null : installedLabel,
                StatusText = family.Summary + " No matching Windows zip was listed on the recent llama.cpp nightlies."
            };
        }

        if (installedBuild is not null && match.RemoteBuild <= installedBuild.Value)
        {
            return new LlamaServerUpdateResult
            {
                InstalledBuildLabel = installedLabel,
                StatusText = family.Summary + " llama-server is current for that family (" + installedLabel + ").",
                ReleaseUrl = match.ReleaseUrl
            };
        }

        if (match.MultipleCudaMinors)
        {
            return new LlamaServerUpdateResult
            {
                NewerAvailable = true,
                InstalledBuildLabel = installedLabel,
                ReleaseUrl = match.ReleaseUrl,
                CudaMinors = match.CudaMinors,
                CudaMinorAmbiguous = true,
                StatusText = family.Summary
                    + " A newer nightly (b" + match.RemoteBuild + ") has more than one CUDA minor zip. Pick the CUDA minor that matches your install (remembered for next time). Do not use a CPU or Vulkan zip. Click Update now to back up and replace the llama-server folder automatically."
            };
        }

        var zipNote = match.ServerZipName is null
            ? " Open the release page and pick the zip for this family."
            : " Matching zip: " + match.ServerZipName + ".";
        var cudartNote = match.CudartZipUrl is null
            ? string.Empty
            : " Matching CUDA DLLs zip: " + match.CudartZipName + ".";

        return new LlamaServerUpdateResult
        {
            NewerAvailable = true,
            InstalledBuildLabel = installedLabel,
            ServerZipUrl = match.ServerZipUrl,
            CudartZipUrl = match.CudartZipUrl,
            ReleaseUrl = match.ReleaseUrl,
            StatusText = family.Summary
                + " This llama-server is " + installedLabel + "; a matching zip is on b" + match.RemoteBuild + "."
                + zipNote
                + cudartNote
                + " Click Update now to back up and replace the llama-server folder automatically."
        };
    }
}
