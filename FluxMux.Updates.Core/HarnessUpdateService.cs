using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace FluxMux.Updates.Core;

public sealed class HarnessUpdateOutcome
{
    public bool Succeeded { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public string? BackupPath { get; init; }
}

/// <summary>
/// Updates DeepSeek Harness via npm and backs up the user's %USERPROFILE%\.dsh config.
/// The npm step is guarded: if npm/npx is not available it reports clearly and does not
/// claim success. The config backup always happens first so a bad update can be reverted.
/// </summary>
public static class HarnessUpdateService
{
    public static HarnessUpdateOutcome UpdateAsync(
        string configDir,
        string backupRoot,
        string? dshPath = null,
        string? targetVersion = null,
        Action<string>? reportProgress = null)
    {
        void Report(string message) => reportProgress?.Invoke(message);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backup = Path.Combine(backupRoot, "dsh-config-" + stamp);

        // 1. Back up the config first, so a bad update is always reversible.
        if (Directory.Exists(configDir))
        {
            Report("Backing up " + configDir + " ...");
            Directory.CreateDirectory(backupRoot);
            foreach (var existing in Directory.GetDirectories(backupRoot))
            {
                try
                {
                    Directory.Delete(existing, recursive: true);
                }
                catch
                {
                }
            }

            try
            {
                Directory.Move(configDir, backup);
            }
            catch (Exception ex)
            {
                return Fail("Could not back up the DeepSeek Harness config (" + configDir + "): " + ex.Message
                    + " Nothing was changed.");
            }
        }
        else
        {
            Report("No existing config folder at " + configDir + "; skipping backup.");
            backup = null;
        }

        // 2. Run the npm update.
        Report("Running the npm update for @deepseek-ai/dsh...");
        var npm = FindOnPath("npm.cmd") ?? FindOnPath("npm");
        if (npm is null)
        {
            return Fail("npm was not found on PATH, so the Harness could not be updated. "
                + "Install Node.js (which provides npm) and try again."
                + (backup is null ? string.Empty : " The previous config is kept at " + backup + "."));
        }

        // Read the version before so we can verify the update actually changed it.
        var versionBefore = DeepSeekHarnessUpdateCheck.ReadInstalled().InstalledVersion;

        // Install the specific version the update check found, if one was supplied.
        // Falling back to "@latest" installs npm's "latest" dist-tag, which may lag
        // behind a newer GitHub prerelease the check just reported.
        var packageSpec = string.IsNullOrWhiteSpace(targetVersion)
            ? "@deepseek-ai/dsh@latest"
            : "@deepseek-ai/dsh@" + targetVersion.Trim();

        Report("Running: npm install -g " + packageSpec);
        var result = RunProcess(npm, "install -g " + packageSpec, out var output);
        if (result.ExitCode != 0)
        {
            // If the specific version isn't on npm (e.g. a GitHub-only prerelease),
            // fall back to @latest so the user still gets the newest stable.
            if (!string.IsNullOrWhiteSpace(targetVersion))
            {
                Report("Version " + targetVersion + " was not available on npm; falling back to @latest.");
                var fallback = RunProcess(npm, "install -g @deepseek-ai/dsh@latest", out var fallbackOutput);
                if (fallback.ExitCode == 0)
                {
                    result = fallback;
                    output = fallbackOutput;
                }
                else
                {
                    return Fail("The npm update finished with exit code " + result.ExitCode + ". "
                        + "The previous config is kept at " + backup + ". Output:\n" + output);
                }
            }
            else
            {
                return Fail("The npm update finished with exit code " + result.ExitCode + ". "
                    + "The previous config is kept at " + backup + ". Output:\n" + output);
            }
        }

        // Verify the version actually changed. npm can exit 0 even when the package is
        // already at the latest version, or when the app uses npx (which downloads its
        // own copy) rather than the global install. Reporting "updated" in those cases
        // would be misleading.
        var versionAfter = DeepSeekHarnessUpdateCheck.ReadInstalled().InstalledVersion;
        var changed = !string.Equals(versionBefore, versionAfter, StringComparison.OrdinalIgnoreCase);

        var versionNote = changed
            ? $"Version changed: {versionBefore ?? "unknown"} -> {versionAfter ?? "unknown"}."
            : $"Version is still {versionAfter ?? "unknown"} (it was already at the latest, or the app uses npx which downloads its own copy).";

        return new HarnessUpdateOutcome
        {
            Succeeded = true,
            BackupPath = backup,
            StatusText = "DeepSeek Harness npm update completed. " + versionNote
                + (backup is null ? string.Empty : " The previous config is kept at " + backup + ".")
        };
    }

    private static HarnessUpdateOutcome Fail(string message) => new() { Succeeded = false, StatusText = message };

    private static (int ExitCode, string Output) RunProcess(string fileName, string arguments, out string output)
    {
        output = string.Empty;
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
            process.WaitForExit();
            output = (stdout + " " + stderr).Trim();
            return (process.ExitCode, output);
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return (-1, output);
        }
    }

    private static string? FindOnPath(string fileName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = OperatingSystem.IsWindows()
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
}
