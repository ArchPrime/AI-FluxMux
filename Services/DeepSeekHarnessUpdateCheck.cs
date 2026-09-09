using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FluxMux.Avalonia.Services;

public sealed class DeepSeekHarnessUpdateResult
{
    public string StatusText { get; init; } = string.Empty;
    public string? InstalledVersion { get; init; }
    public string LaunchMode { get; init; } = string.Empty;
    public bool NpmCheckSucceeded { get; init; }
}

public static class DeepSeekHarnessUpdateCheck
{
    public const string NpmRegistryUrl = "https://registry.npmjs.org/@deepseek-ai%2Fdsh";
    public const string ReleasesUrl = "https://github.com/deepseek-ai/deepseek-harness/releases";

    public static DeepSeekHarnessUpdateResult ReadInstalled()
    {
        var (fileName, argumentPrefix, mode) = DeepSeekHarnessWebHost.ResolveLauncher(null);
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
