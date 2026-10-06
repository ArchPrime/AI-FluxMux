using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FluxMux.Updates.Core;

/// <summary>
/// Outcome of an in-place llama-server update attempt.
/// </summary>
public sealed class LlamaServerUpdateOutcome
{
    public bool Succeeded { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public string? BackupPath { get; init; }
    public int? NewBuild { get; init; }
}

/// <summary>
/// Performs the actual in-place update of the llama-server folder already in use.
/// It downloads the matching Windows zip and the matching CUDA DLLs zip from the
/// llama.cpp nightly that <see cref="LlamaServerUpdateCheck"/> already matched, backs
/// up the current folder, extracts the new files, merges the CUDA runtime DLLs, and
/// restores any DLLs the new zip did not ship. It never deletes the backup.
/// </summary>
public static class LlamaServerUpdateService
{
    private static readonly string[] CudaDllNames =
    [
        "cudart64_13.dll", "cudart64_12.dll", "cudart64_11.dll",
        "cublas64_13.dll", "cublas64_12.dll", "cublas64_11.dll",
        "cublasLt64_13.dll", "cublasLt64_12.dll", "cublasLt64_11.dll"
    ];

    /// <summary>
    /// Resolves the bundled Update-LlamaServer.ps1 next to the running app.
    /// </summary>
    public static string? FindBundledScript()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "scripts", "Update-LlamaServer.ps1"),
            Path.Combine(AppContext.BaseDirectory, "Update-LlamaServer.ps1")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Returns the PIDs of any running llama-server / llama family processes.
    /// </summary>
    public static IReadOnlyList<int> GetRunningLlamaPids()
    {
        var pids = new List<int>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.ProcessName.Contains("llama", StringComparison.OrdinalIgnoreCase)
                    || process.ProcessName.Contains("llserver", StringComparison.OrdinalIgnoreCase))
                {
                    pids.Add(process.Id);
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return pids;
    }

    /// <summary>
    /// Runs the in-place update. <paramref name="serverZipUrl"/> is required;
    /// <paramref name="cudartZipUrl"/> is optional (CUDA runtime DLLs).
    /// Progress is reported through <paramref name="reportProgress"/>.
    /// <paramref name="runningPids"/> lets callers (and tests) supply the running
    /// process check; when null it is read from the live process table.
    /// </summary>
    public static async Task<LlamaServerUpdateOutcome> UpdateAsync(
        HttpClient http,
        string installDir,
        string serverZipUrl,
        string? cudartZipUrl,
        Action<string>? reportProgress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<int>? runningPids = null,
        string? backupIntoFolder = null,
        Action<long, long>? reportBytes = null)
    {
        void Report(string message) => reportProgress?.Invoke(message);

        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            return Fail("The llama-server folder is not set or does not exist. Choose it on the Servers tab first.");
        }

        var exePath = Path.Combine(installDir, "llama-server.exe");
        if (!File.Exists(exePath))
        {
            return Fail("llama-server.exe was not found in the chosen folder.");
        }

        var running = runningPids ?? GetRunningLlamaPids();
        if (running.Count > 0)
        {
            return Fail(
                "llama-server is running (PID " + string.Join(", ", running)
                + "). Stop it in AI-FluxMux first, then run the update again.");
        }

        if (string.IsNullOrWhiteSpace(serverZipUrl))
        {
            return Fail("No matching llama-server zip was found to download. Run Check for updates first.");
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string backup;
        if (!string.IsNullOrWhiteSpace(backupIntoFolder))
        {
            // Dedicated backup folder: wipe whatever was there and store the current
            // install under a timestamped subfolder, so there is always one known place
            // to restore from.
            backup = Path.Combine(backupIntoFolder, "llama-server-" + stamp);
            Directory.CreateDirectory(backupIntoFolder);
            foreach (var existing in Directory.GetDirectories(backupIntoFolder))
            {
                try
                {
                    Directory.Delete(existing, recursive: true);
                }
                catch
                {
                }
            }
        }
        else
        {
            backup = installDir + ".bak-" + stamp;
        }
        var work = Path.Combine(Path.GetTempPath(), "llama-update-" + stamp);
        Directory.CreateDirectory(work);

        try
        {
            Report("Downloading the matching llama-server zip...");
            var serverZip = await DownloadAsync(http, serverZipUrl, Path.GetFileName(new Uri(serverZipUrl).AbsolutePath), work, cancellationToken, reportBytes);

            Report("Extracting the new llama-server...");
            var extract = Path.Combine(work, "extract");
            Directory.CreateDirectory(extract);
            ExtractZip(serverZip, extract);

            var payload = ResolvePayloadDir(extract, "llama-server.exe");
            if (!File.Exists(Path.Combine(payload, "llama-server.exe")))
            {
                return Fail("The downloaded zip did not contain llama-server.exe. Nothing was changed.");
            }

            string? rtPayload = null;
            if (!string.IsNullOrWhiteSpace(cudartZipUrl))
            {
                Report("Downloading the matching CUDA DLLs zip...");
                var cudartZip = await DownloadAsync(http, cudartZipUrl, Path.GetFileName(new Uri(cudartZipUrl).AbsolutePath), work, cancellationToken, reportBytes);
                Report("Extracting the CUDA DLLs...");
                var rtExtract = Path.Combine(work, "cudart");
                Directory.CreateDirectory(rtExtract);
                ExtractZip(cudartZip, rtExtract);
                rtPayload = ResolvePayloadDir(rtExtract, "cudart64_13.dll")
                    ?? ResolvePayloadDir(rtExtract, "cudart64_12.dll")
                    ?? ResolvePayloadDir(rtExtract, "cudart64_11.dll");
            }

            Report("Backing up the current install...");
            Directory.Move(installDir, backup);
            Directory.CreateDirectory(installDir);

            Report("Installing the new llama-server...");
            CopyTree(payload, installDir);

            if (rtPayload is not null)
            {
                Report("Merging the CUDA runtime DLLs...");
                CopyTree(rtPayload, installDir);
            }

            Report("Restoring any CUDA DLLs the new zip did not ship...");
            foreach (var dll in CudaDllNames)
            {
                var from = Path.Combine(backup, dll);
                var to = Path.Combine(installDir, dll);
                if (File.Exists(from) && !File.Exists(to))
                {
                    File.Copy(from, to);
                }
            }

            var newBuild = LlamaCppBuildNumber.TryParse(LlamaServerUpdateCheck.ReadVersionText(Path.Combine(installDir, "llama-server.exe")));
            return new LlamaServerUpdateOutcome
            {
                Succeeded = true,
                BackupPath = backup,
                NewBuild = newBuild,
                StatusText = "llama-server updated"
                    + (newBuild is null ? string.Empty : " to build b" + newBuild.Value)
                    + ". The previous install is kept at " + backup
                    + ". Launch a known local model profile to confirm it still works."
            };
        }
        catch (Exception ex)
        {
            return Fail("The update stopped before finishing: " + ex.Message
                + " The previous install is kept at " + backup + ". You can point Servers back at it if needed.");
        }
        finally
        {
            try
            {
                if (Directory.Exists(work))
                {
                    Directory.Delete(work, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private static LlamaServerUpdateOutcome Fail(string message)
        => new() { Succeeded = false, StatusText = message };

    private static async Task<string> DownloadAsync(
        HttpClient http,
        string url,
        string fileName,
        string workDir,
        CancellationToken cancellationToken,
        Action<long, long>? reportBytes = null)
    {
        var target = Path.Combine(workDir, SanitizeFileName(fileName));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(FluxMuxAppInfo.UserAgent);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(target);
        var buffer = new byte[81920];
        var read = 0L;
        int n;
        while ((n = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
            read += n;
            reportBytes?.Invoke(read, total);
        }

        return target;
    }

    private static void ExtractZip(string zipPath, string destination)
    {
        ZipFile.ExtractToDirectory(zipPath, destination, overwriteFiles: true);
    }

    private static string ResolvePayloadDir(string root, string mustContain)
    {
        if (File.Exists(Path.Combine(root, mustContain)))
        {
            return root;
        }

        foreach (var nested in Directory.GetDirectories(root))
        {
            if (File.Exists(Path.Combine(nested, mustContain)))
            {
                return nested;
            }
        }

        return root;
    }

    private static void CopyTree(string sourceDir, string destinationDir)
    {
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var target = Path.Combine(destinationDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }
}