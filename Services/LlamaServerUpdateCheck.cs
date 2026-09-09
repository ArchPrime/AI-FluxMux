using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FluxMux.Avalonia.Services;

public sealed class LlamaServerUpdateResult
{
    public string StatusText { get; init; } = string.Empty;
    public string? ServerZipUrl { get; init; }
    public string? CudartZipUrl { get; init; }
    public string? ReleaseUrl { get; init; }
    public bool NewerAvailable { get; init; }
}

public static class LlamaServerUpdateCheck
{
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
        CancellationToken cancellationToken = default)
    {
        var family = LlamaServerFamilyFingerprint.FromInstallDirectory(executablePath);
        if (!family.CanMatch)
        {
            return new LlamaServerUpdateResult { StatusText = family.Summary };
        }

        var versionText = ReadVersionText(executablePath ?? string.Empty);
        var installedBuild = LlamaCppBuildNumber.TryParse(versionText);
        string json;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GithubReleasesUrl);
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
                StatusText = family.Summary + " Could not read llama.cpp releases: " + ex.Message
            };
        }

        var releases = LlamaCppReleaseMatcher.ParseReleases(json);
        var match = LlamaCppReleaseMatcher.FindNewerMatching(releases, family, installedBuild);
        if (match is null)
        {
            return new LlamaServerUpdateResult
            {
                StatusText = family.Summary + " No matching Windows zip was listed on the recent llama.cpp nightlies."
            };
        }

        var installedLabel = installedBuild is null ? "unknown build" : "b" + installedBuild.Value;
        if (installedBuild is not null && match.RemoteBuild <= installedBuild.Value)
        {
            return new LlamaServerUpdateResult
            {
                StatusText = family.Summary + " llama-server is current for that family (" + installedLabel + ").",
                ReleaseUrl = match.ReleaseUrl
            };
        }

        if (match.MultipleCudaMinors)
        {
            return new LlamaServerUpdateResult
            {
                NewerAvailable = true,
                ReleaseUrl = match.ReleaseUrl,
                StatusText = family.Summary
                    + " A newer nightly (b" + match.RemoteBuild + ") has more than one CUDA minor zip. Open the release and pick the same CUDA minor you extracted before. Do not use a CPU or Vulkan zip. Extract into a new folder; keep this install until Launch works."
            };
        }

        var zipNote = match.ServerZipName is null
            ? " Open the release page and pick the zip for this family."
            : " Matching zip: " + match.ServerZipName + ".";
        var cudartNote = match.CudartZipName is null
            ? string.Empty
            : " Matching CUDA DLLs zip: " + match.CudartZipName + ".";

        return new LlamaServerUpdateResult
        {
            NewerAvailable = true,
            ServerZipUrl = match.ServerZipUrl,
            CudartZipUrl = match.CudartZipUrl,
            ReleaseUrl = match.ReleaseUrl,
            StatusText = family.Summary
                + " This llama-server is " + installedLabel + "; a matching zip is on b" + match.RemoteBuild + "."
                + zipNote
                + cudartNote
                + " Extract into a new folder. Point Servers at the new llama-server.exe only after Launch works. Keep the old folder."
        };
    }
}
