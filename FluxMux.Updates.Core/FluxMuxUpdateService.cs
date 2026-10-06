using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FluxMux.Updates.Core;

public sealed class FluxMuxUpdateOutcome
{
    public bool Succeeded { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public string? BackupPath { get; init; }
}

/// <summary>
/// Performs an in-place update of the AI-FluxMux install folder. Downloads the newer
/// build zip from the update feed, backs up the current install into a dedicated backup
/// folder (wiping its previous contents), clears the current folder, and extracts the
/// new build in place. The caller must ensure AI-FluxMux is not running (Windows locks
/// the running .exe/.dll, so this only works while the app is closed).
/// </summary>
public static class FluxMuxUpdateService
{
    public static async Task<FluxMuxUpdateOutcome> UpdateAsync(
        HttpClient http,
        string installDir,
        string downloadUrl,
        Action<string>? reportProgress = null,
        CancellationToken cancellationToken = default,
        string? backupIntoFolder = null,
        Action<long, long>? reportBytes = null)
    {
        void Report(string message) => reportProgress?.Invoke(message);

        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            return Fail("The AI-FluxMux install folder was not found.");
        }

        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            return Fail("No AI-FluxMux download URL was available. Run a check for updates first.");
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string backup;
        if (!string.IsNullOrWhiteSpace(backupIntoFolder))
        {
            backup = Path.Combine(backupIntoFolder, "AI-FluxMux-" + stamp);
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

        var work = Path.Combine(Path.GetTempPath(), "fluxmux-update-" + stamp);
        Directory.CreateDirectory(work);

        try
        {
            Report("Downloading the new AI-FluxMux build...");
            var zipName = Path.GetFileName(new Uri(downloadUrl).AbsolutePath);
            if (string.IsNullOrWhiteSpace(zipName) || !zipName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                zipName = "AI-FluxMux.zip";
            }

            var zipPath = await DownloadAsync(http, downloadUrl, zipName, work, cancellationToken, reportBytes);

            Report("Extracting the new build...");
            var extract = Path.Combine(work, "extract");
            Directory.CreateDirectory(extract);
            ZipFile.ExtractToDirectory(zipPath, extract, overwriteFiles: true);

            var payload = ResolvePayloadDir(extract, "AI-FluxMux.exe")
                ?? ResolvePayloadDir(extract, "FluxMux.Avalonia.exe")
                ?? ResolvePayloadDir(extract, "FluxMux.Avalonia.dll");
            if (payload is null)
            {
                return Fail("The downloaded build did not contain the AI-FluxMux executable. Nothing was changed.");
            }

            Report("Backing up the current install...");
            Directory.Move(installDir, backup);
            Directory.CreateDirectory(installDir);

            Report("Installing the new build...");
            CopyTree(payload, installDir);

            return new FluxMuxUpdateOutcome
            {
                Succeeded = true,
                BackupPath = backup,
                StatusText = "AI-FluxMux updated. The previous install is kept at " + backup
                    + ". Restart AI-FluxMux to use the new build."
            };
        }
        catch (Exception ex)
        {
            return Fail("The update stopped before finishing: " + ex.Message
                + " The previous install is kept at " + backup + ".");
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

    private static FluxMuxUpdateOutcome Fail(string message) => new() { Succeeded = false, StatusText = message };

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

    private static string? ResolvePayloadDir(string root, params string[] markers)
    {
        foreach (var marker in markers)
        {
            if (File.Exists(Path.Combine(root, marker)))
            {
                return root;
            }

            foreach (var nested in Directory.GetDirectories(root))
            {
                if (File.Exists(Path.Combine(nested, marker)))
                {
                    return nested;
                }
            }
        }

        return null;
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
