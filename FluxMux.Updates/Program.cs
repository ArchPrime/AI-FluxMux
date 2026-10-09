using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using FluxMux.Updates.Core;

namespace FluxMux.Updates;

/// <summary>
/// Standalone updater for AI-FluxMux, llama-server, and DeepSeek Harness.
///
/// Run ONLY while AI-FluxMux is closed. Each subcommand re-runs the same detection
/// logic the app uses (shared via FluxMux.Updates.Core), so it knows the current
/// version and what should replace it. On success it prints a report describing what
/// it did and how to recover, and offers to restart AI-FluxMux (or cancel).
///
/// Usage:
///   FluxMux.Updates.exe llama --server-dir &lt;path-to-llama-server-folder&gt;
///   FluxMux.Updates.exe fluxmux --install-dir &lt;path-to-AI-FluxMux-folder&gt; --feed-url &lt;url&gt;
///   FluxMux.Updates.exe harness [--dsh-path &lt;path&gt;]
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        var command = args[0].Trim().ToLowerInvariant();
        var options = ParseOptions(args.Skip(1).ToArray());

        // 'help' and unknown commands print to the console (no GUI needed).
        if (command is "help" or "--help" or "-h")
        {
            PrintUsage();
            return 0;
        }

        if (command is not ("llama" or "fluxmux" or "harness"))
        {
            Console.Error.WriteLine($"Unknown command '{command}'.");
            Console.WriteLine();
            PrintUsage();
            return 2;
        }

        // Run the update in a GUI progress dialog.
        ApplicationConfiguration.Initialize();
        using var form = new UpdaterForm(TitleFor(command));
        form.Show();

        var exitCode = 0;
        var worker = new Thread(() =>
        {
            // Redirect console output into the form so the existing command code
            // (which uses Console.WriteLine) drives the progress UI.
            using var redirect = new ConsoleRedirect(form);
            try
            {
                exitCode = command switch
                {
                    "llama" => LlamaCommand(options, form),
                    "fluxmux" => FluxMuxCommand(options, form),
                    "harness" => HarnessCommand(options, form),
                    _ => 2
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Unexpected error: " + ex.Message);
                exitCode = 1;
            }

            // Safety net: some early-return paths (missing args, up to date, validation
            // failure) exit without calling Finish. Make sure the dialog always resolves
            // to a clickable button instead of hanging on the progress bar.
            if (!form.IsFinished)
            {
                form.Finish(
                    exitCode == 0,
                    exitCode == 0 ? "Done" : "Update did not complete",
                    exitCode == 0 ? "Nothing further to do." : "See the details above for what happened.");
            }
        });
        worker.IsBackground = true;
        worker.Start();

        Application.Run(form);
        return exitCode;
    }

    private static string TitleFor(string command) => command switch
    {
        "llama" => "Updating llama-server",
        "fluxmux" => "Updating AI-FluxMux",
        "harness" => "Updating DeepSeek Harness",
        _ => "AI-FluxMux Updater"
    };

    private static void PrintUsage()
    {
        Console.WriteLine("Commands:");
        Console.WriteLine("  llama     Update llama-server in place (backup + replace).");
        Console.WriteLine("            Requires: --server-dir <folder containing llama-server.exe>");
        Console.WriteLine("  fluxmux   Update AI-FluxMux in place (backup + replace).");
        Console.WriteLine("            Requires: --install-dir <folder containing AI-FluxMux.exe> --feed-url <url>");
        Console.WriteLine("  harness   Update DeepSeek Harness via npm (backs up %USERPROFILE%\\.dsh).");
        Console.WriteLine("            Optional: --dsh-path <custom dsh executable>");
        Console.WriteLine();
        Console.WriteLine("Always close AI-FluxMux before running. On success, you'll be offered to restart it.");
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (token.StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                options[token.TrimStart('-')] = args[++i];
            }
        }

        return options;
    }
    // ---------------------------------------------------------------------
    // llama-server
    // ---------------------------------------------------------------------

    private static int LlamaCommand(Dictionary<string, string> options, UpdaterForm? form)
    {
        if (!options.TryGetValue("server-dir", out var serverDir) || string.IsNullOrWhiteSpace(serverDir))
        {
            Console.Error.WriteLine("llama: --server-dir is required (the folder containing llama-server.exe).");
            return 2;
        }

        serverDir = Path.GetFullPath(serverDir);
        var exePath = Path.Combine(serverDir, "llama-server.exe");
        if (!File.Exists(exePath))
        {
            Console.Error.WriteLine($"llama: llama-server.exe was not found in '{serverDir}'.");
            return 2;
        }

        Console.WriteLine($"llama-server folder: {serverDir}");
        Console.WriteLine();

        if (!EnsureAiFluxMuxNotRunning(form, TimeSpan.FromSeconds(60)))
        {
            form?.Finish(false, "AI-FluxMux is still running", "Close it, then run the updater again.");
            return 1;
        }

        var running = LlamaServerUpdateService.GetRunningLlamaPids();
        if (running.Count > 0)
        {
            Console.Error.WriteLine("llama: llama-server is running (PID " + string.Join(", ", running) + "). Stop it first.");
            return 1;
        }

        Console.WriteLine("Detecting the current install and the newest matching nightly...");
        var family = LlamaServerFamilyFingerprint.FromInstallDirectory(exePath);
        if (!family.CanMatch)
        {
            Console.Error.WriteLine("llama: " + family.Summary);
            Console.Error.WriteLine("Nothing was changed.");
            return 1;
        }

        var installedBuild = LlamaCppBuildNumber.TryParse(LlamaServerUpdateCheck.ReadVersionText(exePath));
        Console.WriteLine($"  install family: {family.Summary}");
        Console.WriteLine($"  installed build: {(installedBuild is null ? "unknown" : "b" + installedBuild.Value)}");
        Console.WriteLine();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(FluxMuxAppInfo.UserAgent);

        LlamaServerUpdateMatch? match;
        try
        {
            var releases = LlamaCppReleaseMatcher.ParseReleases(FetchReleasesJson(http));
            var preferredCudaMinor = options.TryGetValue("cuda-minor", out var cm) ? cm : null;
            match = LlamaCppReleaseMatcher.FindNewerMatching(releases, family, installedBuild, preferredCudaMinor);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("llama: could not read llama.cpp releases: " + ex.Message);
            return 1;
        }

        if (match is null)
        {
            Console.WriteLine("llama: no matching Windows zip was listed on the recent llama.cpp nightlies. Nothing to do.");
            return 0;
        }

        if (match.RemoteBuild <= (installedBuild ?? 0))
        {
            Console.WriteLine($"llama: your install (b{installedBuild}) is already current for that family (newest matching nightly is b{match.RemoteBuild}). Nothing to do.");
            return 0;
        }

        if (match.MultipleCudaMinors)
        {
            Console.Error.WriteLine("llama: a newer nightly (b" + match.RemoteBuild + ") has more than one CUDA minor zip, and this install's exact CUDA minor cannot be determined automatically.");
            Console.Error.WriteLine("Open the release and pick the same CUDA minor you installed before:");
            Console.Error.WriteLine("  " + match.ReleaseUrl);
            Console.Error.WriteLine("Nothing was changed.");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(match.ServerZipUrl))
        {
            Console.Error.WriteLine("llama: a newer build was found but no direct zip URL was available. Open the release page: " + match.ReleaseUrl);
            return 1;
        }

        var backupRoot = Path.Combine(Path.GetDirectoryName(serverDir) ?? serverDir, "llama-server-backup");
        Console.WriteLine($"Updating llama-server: b{(installedBuild ?? 0)} -> b{match.RemoteBuild}");
        Console.WriteLine($"  new zip: {match.ServerZipName}");
        if (!string.IsNullOrWhiteSpace(match.CudartZipName))
        {
            Console.WriteLine($"  CUDA DLLs zip: {match.CudartZipName}");
        }
        Console.WriteLine($"  backup folder: {backupRoot} (existing contents will be replaced)");
        Console.WriteLine();

        var outcome = LlamaServerUpdateService.UpdateAsync(
            http,
            serverDir,
            match.ServerZipUrl,
            string.IsNullOrWhiteSpace(match.CudartZipUrl) ? null : match.CudartZipUrl,
            reportProgress: message =>
            {
                Console.WriteLine("  " + message);
                form?.SetStatus(message);
            },
            backupIntoFolder: backupRoot,
            reportBytes: (done, total) => form?.SetProgress(done, total)).GetAwaiter().GetResult();

        PrintLlamaReport(outcome, serverDir, backupRoot, match.RemoteBuild, installedBuild, form);
        return outcome.Succeeded ? 0 : 1;
    }
    private static void PrintLlamaReport(
        LlamaServerUpdateOutcome outcome,
        string serverDir,
        string backupRoot,
        int targetBuild,
        int? installedBuild,
        UpdaterForm? form)
    {
        Console.WriteLine();
        Console.WriteLine("=== What happened ===");
        if (outcome.Succeeded)
        {
            Console.WriteLine($"llama-server was updated to b{targetBuild} in {serverDir}.");
            Console.WriteLine($"The previous install was backed up to: {outcome.BackupPath}");
        }
        else
        {
            Console.WriteLine("The update stopped before finishing. " + outcome.StatusText);
        }

        Console.WriteLine();
        Console.WriteLine("=== How to recover if something is wrong ===");
        Console.WriteLine("1. Close AI-FluxMux.");
        Console.WriteLine($"2. Delete the current folder: {serverDir}");
        Console.WriteLine($"3. Restore the backup: move the folder at {outcome.BackupPath} back to {serverDir}");
        Console.WriteLine("4. Restart AI-FluxMux and confirm Launch works.");
        Console.WriteLine();
        if (outcome.Succeeded)
        {
            Console.WriteLine("Success. The updated llama-server is ready.");
            form?.Finish(true, "Update complete", "Previous install backed up to: " + outcome.BackupPath);
        }
        else
        {
            form?.Finish(false, "Update failed", outcome.StatusText);
        }
    }

    // ---------------------------------------------------------------------
    // AI-FluxMux
    // ---------------------------------------------------------------------

    private static int FluxMuxCommand(Dictionary<string, string> options, UpdaterForm? form)
    {
        if (!options.TryGetValue("install-dir", out var installDir) || string.IsNullOrWhiteSpace(installDir))
        {
            Console.Error.WriteLine("fluxmux: --install-dir is required (the folder containing AI-FluxMux.exe).");
            return 2;
        }

        if (!options.TryGetValue("feed-url", out var feedUrl) || string.IsNullOrWhiteSpace(feedUrl))
        {
            Console.Error.WriteLine("fluxmux: --feed-url is required (the AI-FluxMux update feed JSON).");
            return 2;
        }

        installDir = Path.GetFullPath(installDir);
        if (!Directory.Exists(installDir))
        {
            Console.Error.WriteLine($"fluxmux: install folder not found: {installDir}");
            return 2;
        }

        Console.WriteLine($"AI-FluxMux install folder: {installDir}");
        Console.WriteLine();

        if (!EnsureAiFluxMuxNotRunning(form, TimeSpan.FromSeconds(60)))
        {
            form?.Finish(false, "AI-FluxMux is still running", "Close it, then run the updater again.");
            return 1;
        }

        Console.WriteLine("Reading the update feed...");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(FluxMuxAppInfo.UserAgent);

        var result = UpdateCheck.RunAsync(
            http,
            feedUrl,
            FluxMuxAppInfo.Version,
            Path.Combine(installDir, "Help.html"),
            null,
            CancellationToken.None).GetAwaiter().GetResult();

        if (!result.AppUpdateAvailable || string.IsNullOrWhiteSpace(result.DownloadUrl))
        {
            Console.WriteLine("fluxmux: " + result.StatusText);
            Console.WriteLine("Nothing to do.");
            return 0;
        }

        var backupRoot = Path.Combine(Path.GetDirectoryName(installDir) ?? installDir, "AI-FluxMux-backup");
        Console.WriteLine($"Updating AI-FluxMux: {FluxMuxAppInfo.Version} -> newer build from feed.");
        Console.WriteLine($"  download: {result.DownloadUrl}");
        Console.WriteLine($"  backup folder: {backupRoot} (existing contents will be replaced)");
        Console.WriteLine();

        var outcome = FluxMuxUpdateService.UpdateAsync(
            http,
            installDir,
            result.DownloadUrl,
            reportProgress: message =>
            {
                Console.WriteLine("  " + message);
                form?.SetStatus(message);
            },
            backupIntoFolder: backupRoot,
            reportBytes: (done, total) => form?.SetProgress(done, total)).GetAwaiter().GetResult();

        PrintFluxMuxReport(outcome, installDir, backupRoot, form);
        return outcome.Succeeded ? 0 : 1;
    }

    private static void PrintFluxMuxReport(FluxMuxUpdateOutcome outcome, string installDir, string backupRoot, UpdaterForm? form)
    {
        Console.WriteLine();
        Console.WriteLine("=== What happened ===");
        if (outcome.Succeeded)
        {
            Console.WriteLine($"AI-FluxMux was updated in {installDir}.");
            Console.WriteLine($"The previous install was backed up to: {outcome.BackupPath}");
        }
        else
        {
            Console.WriteLine("The update stopped before finishing. " + outcome.StatusText);
        }

        Console.WriteLine();
        Console.WriteLine("=== How to recover if something is wrong ===");
        Console.WriteLine("1. Close AI-FluxMux.");
        Console.WriteLine($"2. Delete the current folder: {installDir}");
        Console.WriteLine($"3. Restore the backup: move the folder at {outcome.BackupPath} back to {installDir}");
        Console.WriteLine("4. Restart AI-FluxMux.");
        Console.WriteLine();
        if (outcome.Succeeded)
        {
            Console.WriteLine("Success. The updated AI-FluxMux build is ready.");
            form?.Finish(true, "Update complete", "Previous install backed up to: " + outcome.BackupPath);
        }
        else
        {
            form?.Finish(false, "Update failed", outcome.StatusText);
        }
    }

    // ---------------------------------------------------------------------
    // DeepSeek Harness
    // ---------------------------------------------------------------------

    private static int HarnessCommand(Dictionary<string, string> options, UpdaterForm? form)
    {
        Console.WriteLine("DeepSeek Harness update");
        Console.WriteLine();

        if (!EnsureAiFluxMuxNotRunning(form, TimeSpan.FromSeconds(60)))
        {
            form?.Finish(false, "AI-FluxMux is still running", "Close AI-FluxMux before running the updater.");
            return 1;
        }

        var dshPath = options.TryGetValue("dsh-path", out var p) ? p : null;
        var installed = DeepSeekHarnessUpdateCheck.ReadInstalled();
        Console.WriteLine($"  installed version: {(string.IsNullOrWhiteSpace(installed.InstalledVersion) ? "unknown" : installed.InstalledVersion)}");
        Console.WriteLine($"  launch mode: {installed.LaunchMode}");
        Console.WriteLine();

        // Find the target version the same way the app's "Check for updates" does:
        // look at the latest GitHub release. This may be a prerelease that npm's
        // "latest" dist-tag hasn't caught up to yet, so we install that specific
        // version rather than blindly using @latest.
        string? targetVersion = null;
        form?.SetStatus("Checking the latest DeepSeek Harness release...");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(FluxMuxAppInfo.UserAgent);
            var check = DeepSeekHarnessUpdateCheck.CheckGithubAsync(http).GetAwaiter().GetResult();
            if (check.NewerAvailable && !string.IsNullOrWhiteSpace(check.LatestVersion))
            {
                targetVersion = check.LatestVersion;
                Console.WriteLine($"  latest GitHub release: {check.LatestVersion} (newer than installed {check.InstalledVersion})");
            }
            else
            {
                Console.WriteLine($"  GitHub's latest release is {(string.IsNullOrWhiteSpace(check.LatestVersion) ? "unknown" : check.LatestVersion)}; no newer version than the installed one.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  Could not check GitHub for the latest release (" + ex.Message + "); will use npm's @latest instead.");
        }
        Console.WriteLine();

        var configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        var backupRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "deepseek-harness-backup");

        Console.WriteLine("This will:");
        Console.WriteLine($"  1. Back up {configDir} to {backupRoot}\\dsh-config (existing contents replaced).");
        Console.WriteLine("  2. Run the npm update for @deepseek-ai/dsh.");
        Console.WriteLine();

        var outcome = HarnessUpdateService.UpdateAsync(
            configDir,
            backupRoot,
            dshPath,
            targetVersion,
            reportProgress: message =>
            {
                Console.WriteLine("  " + message);
                form?.SetStatus(message);
            });

        PrintHarnessReport(outcome, configDir, backupRoot, form);
        return outcome.Succeeded ? 0 : 1;
    }

    private static void PrintHarnessReport(HarnessUpdateOutcome outcome, string configDir, string backupRoot, UpdaterForm? form)
    {
        Console.WriteLine();
        Console.WriteLine("=== What happened ===");
        if (outcome.Succeeded)
        {
            Console.WriteLine("DeepSeek Harness was updated via npm.");
            Console.WriteLine($"The previous config was backed up to: {outcome.BackupPath}");
        }
        else
        {
            Console.WriteLine("The update stopped before finishing. " + outcome.StatusText);
        }

        Console.WriteLine();
        Console.WriteLine("=== How to recover if something is wrong ===");
        Console.WriteLine("1. Close AI-FluxMux.");
        Console.WriteLine($"2. Delete the current config folder: {configDir}");
        Console.WriteLine($"3. Restore the backup: move the folder at {outcome.BackupPath} back to {configDir}");
        Console.WriteLine("4. To roll back the npm package: 'npm install -g @deepseek-ai/dsh@<previous-version>'.");
        Console.WriteLine();
        if (outcome.Succeeded)
        {
            Console.WriteLine("Success. The updated DeepSeek Harness is ready.");
            form?.Finish(true, "Update complete", "Previous config backed up to: " + outcome.BackupPath);
        }
        else
        {
            form?.Finish(false, "Update failed", outcome.StatusText);
        }
    }

    // ---------------------------------------------------------------------
    // shared helpers
    // ---------------------------------------------------------------------

    private static string FetchReleasesJson(HttpClient http)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LlamaServerUpdateCheck.GithubReleasesUrl);
        request.Headers.UserAgent.ParseAdd(FluxMuxAppInfo.UserAgent);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = http.Send(request);
        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Waits (up to <paramref name="timeout"/>) for the AI-FluxMux app process to exit,
    /// then returns true. The app launches the updater and then closes itself, so the
    /// updater usually has to wait a moment for the app's process to actually terminate.
    /// Returns false if the app is still running after the timeout.
    /// </summary>
    private static bool EnsureAiFluxMuxNotRunning(UpdaterForm? form, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var waited = false;
        while (true)
        {
            if (FindAiFluxMuxPids().Count == 0)
            {
                return true;
            }

            if (!waited)
            {
                form?.SetStatus("Waiting for AI-FluxMux to close...");
                waited = true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                var pids = FindAiFluxMuxPids();
                Console.Error.WriteLine("AI-FluxMux is still running (PID " + string.Join(", ", pids) + "). Close it, then run the updater again.");
                return false;
            }

            Thread.Sleep(500);
        }
    }

    private static List<int> FindAiFluxMuxPids()
    {
        var pids = new List<int>();
        var selfPid = System.Diagnostics.Process.GetCurrentProcess().Id;
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                // Match the app's exact process name. A substring match like "FluxMux"
                // would also catch this updater (FluxMux.Updates) and false-positive.
                if (process.Id != selfPid
                    && string.Equals(process.ProcessName, "FluxMux.Avalonia", StringComparison.OrdinalIgnoreCase))
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
}

/// <summary>
/// Redirects <see cref="Console.Out"/> and <see cref="Console.Error"/> to an
/// <see cref="UpdaterForm"/> so the existing command code (which writes to the console)
/// drives the GUI progress dialog. Each line becomes the form's detail text; the most
/// recent non-empty line also updates the status label.
/// </summary>
internal sealed class ConsoleRedirect : IDisposable
{
    private readonly UpdaterForm _form;
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;

    public ConsoleRedirect(UpdaterForm form)
    {
        _form = form;
        _originalOut = Console.Out;
        _originalError = Console.Error;
        var writer = new FormTextWriter(_form);
        Console.SetOut(writer);
        Console.SetError(writer);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
    }

    private sealed class FormTextWriter : TextWriter
    {
        private readonly UpdaterForm _form;
        private readonly StringBuilder _line = new();

        public FormTextWriter(UpdaterForm form) => _form = form;

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            _line.Append(value);
        }

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            _line.Append(value);
        }

        public override void WriteLine()
        {
            FlushLine();
        }

        public override void WriteLine(string? value)
        {
            _line.Append(value ?? string.Empty);
            FlushLine();
        }

        private void FlushLine()
        {
            var text = _line.ToString().Trim();
            _line.Clear();
            if (text.Length == 0) return;
            _form.SetDetail(text);
            // Keep the status label showing the latest meaningful step (skip the "===" headers).
            if (!text.StartsWith("===", StringComparison.Ordinal))
            {
                _form.SetStatus(text.TrimStart(' ', '\t'));
            }
        }
    }
}

