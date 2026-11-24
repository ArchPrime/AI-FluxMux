using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace FluxMux.Avalonia.Services.HailMary;

/// <summary>The stuck chat a rescue adapter found.</summary>
public sealed record RescueSession(string SessionId, string SessionDir, string SessionFile);

/// <summary>Client-app actions the engine cannot do generically (stop and reopen Harness web).</summary>
public interface IRescueHost
{
    /// <summary>Runs a named stop or reopen action such as "harness-web". False when it failed.</summary>
    Task<bool> RunNamedActionAsync(string kind, bool reopen, RescueSession session, CancellationToken cancellationToken);
}

public sealed record RescueRunResult(bool Success, string Message, IReadOnlyList<string> Log);

/// <summary>
/// Runs a <see cref="RescueAdapterDefinition"/>: find → stop → back up → steps → reopen. Every
/// Client-app specific is in the definition; the engine only knows the step kinds.
/// </summary>
public static class RescueEngine
{
    /// <summary>The newest chat file matching the adapter's Find rule.</summary>
    public static RescueSession? FindSession(RescueFind find)
    {
        try
        {
            var root = Environment.ExpandEnvironmentVariables(find.Root ?? string.Empty);
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return null;
            }

            IEnumerable<string> dirs = [root];
            foreach (var segment in (find.Dirs ?? "*").Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            {
                var pattern = segment;
                dirs = dirs.SelectMany(d => SafeDirs(d, pattern)).ToList();
            }

            RescueSession? best = null;
            var bestTime = DateTime.MinValue;
            foreach (var dir in dirs)
            {
                foreach (var file in SafeFiles(dir, string.IsNullOrWhiteSpace(find.File) ? "*" : find.File))
                {
                    var written = File.GetLastWriteTimeUtc(file);
                    if (written > bestTime)
                    {
                        bestTime = written;
                        best = new RescueSession(Path.GetFileName(dir), dir, file);
                    }
                }
            }

            return best;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Folder the app's own rescue helpers are copied to ({rescueScripts}).</summary>
    public static string DefaultScriptsFolder => Path.Combine(AppContext.BaseDirectory, "Rescue");

    public static string Expand(string template, IReadOnlyDictionary<string, string> values)
    {
        var text = template ?? string.Empty;
        foreach (var pair in values)
        {
            text = text.Replace("{" + pair.Key + "}", pair.Value, StringComparison.Ordinal);
        }

        return Environment.ExpandEnvironmentVariables(text);
    }

    public static Dictionary<string, string> Placeholders(
        RescueAdapterDefinition adapter,
        RescueSession session,
        string summary,
        string summaryFile,
        string? scriptsFolder = null) => new(StringComparer.Ordinal)
    {
        ["summary"] = summary,
        ["summaryJson"] = JsonSerializer.Serialize(summary),
        ["summaryFile"] = summaryFile,
        ["sessionId"] = session.SessionId,
        ["sessionDir"] = session.SessionDir,
        ["sessionFile"] = session.SessionFile,
        ["keepRecent"] = adapter.KeepRecent.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["rescueScripts"] = scriptsFolder ?? DefaultScriptsFolder,
        ["nowMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Runs every step of a non-built-in adapter. The built-in Cline writer is run by its own
    /// strategy; a "builtin" step here fails so a misconfigured adapter cannot silently no-op.
    /// </summary>
    public static async Task<RescueRunResult> RunAsync(
        RescueAdapterDefinition adapter,
        RescueSession session,
        string summary,
        IRescueHost? host,
        string? scriptsFolder = null,
        CancellationToken cancellationToken = default)
    {
        var log = new List<string>();
        var summaryFile = Path.Combine(Path.GetTempPath(), "fluxmux-hailmary-" + Guid.NewGuid().ToString("N") + ".md");
        var stopped = false;
        try
        {
            File.WriteAllText(summaryFile, summary, new UTF8Encoding(false));
            var values = Placeholders(adapter, session, summary, summaryFile, scriptsFolder);

            if (!adapter.Stop.IsNone)
            {
                log.Add("stop: " + adapter.Stop.Kind);
                if (!await RunActionAsync(adapter.Stop, reopen: false, session, values, host, log, cancellationToken).ConfigureAwait(false))
                {
                    return new RescueRunResult(false, "Could not stop the Client app, so its chat file was not changed.", log);
                }

                stopped = true;
            }

            if (adapter.Backup && File.Exists(session.SessionFile))
            {
                var backup = session.SessionFile + ".hailmary-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".bak";
                File.Copy(session.SessionFile, backup, overwrite: false);
                log.Add("backup: " + backup);
            }

            foreach (var step in adapter.Steps)
            {
                var failure = await RunStepAsync(step, values, log, cancellationToken).ConfigureAwait(false);
                if (failure is not null)
                {
                    if (stopped && !adapter.Reopen.IsNone)
                    {
                        // Bring the Client app back on the untouched chat rather than leave it stopped.
                        await RunActionAsync(adapter.Reopen, reopen: true, session, values, host, log, cancellationToken).ConfigureAwait(false);
                    }

                    return new RescueRunResult(false, failure, log);
                }
            }

            if (!adapter.Reopen.IsNone)
            {
                log.Add("reopen: " + adapter.Reopen.Kind);
                if (!await RunActionAsync(adapter.Reopen, reopen: true, session, values, host, log, cancellationToken).ConfigureAwait(false))
                {
                    return new RescueRunResult(true, "The chat was rebuilt, but the Client app could not be reopened by itself. Open it and go back to the same chat.", log);
                }
            }

            return new RescueRunResult(true, string.IsNullOrWhiteSpace(adapter.DoneMessage) ? "The chat was rebuilt." : adapter.DoneMessage, log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log.Add("error: " + ex.Message);
            return new RescueRunResult(false, "The rescue stopped: " + ex.Message, log);
        }
        finally
        {
            try
            {
                File.Delete(summaryFile);
            }
            catch
            {
            }
        }
    }

    private static async Task<string?> RunStepAsync(RescueStep step, IReadOnlyDictionary<string, string> values, List<string> log, CancellationToken cancellationToken)
    {
        switch (step.Kind.ToLowerInvariant())
        {
            case RescueStep.WriteFileKind:
            {
                var path = Expand(step.Path, values);
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                File.WriteAllText(path, Expand(step.Content, values), new UTF8Encoding(false));
                log.Add("write-file: " + path);
                return null;
            }
            case RescueStep.DeleteKind:
            {
                var path = Expand(step.Path, values);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                log.Add("delete: " + path);
                return null;
            }
            case RescueStep.CommandKind:
                return await RunCommandStepAsync(step, values, log, cancellationToken).ConfigureAwait(false);
            default:
                log.Add("unknown step: " + step.Kind + " " + step.Name);
                return "This rescue has a step AI-FluxMux does not know (" + step.Kind + " " + step.Name + ").";
        }
    }

    private static async Task<string?> RunCommandStepAsync(RescueStep step, IReadOnlyDictionary<string, string> values, List<string> log, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Expand(step.Exe, values),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in step.Args)
        {
            psi.ArgumentList.Add(Expand(arg, values));
        }

        log.Add("command: " + psi.FileName + " " + string.Join(" ", psi.ArgumentList.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a)));
        using var process = Process.Start(psi);
        if (process is null)
        {
            return "Could not start " + psi.FileName + ".";
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(step.TimeoutSeconds));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            return psi.FileName + " did not finish within " + step.TimeoutSeconds + " s.";
        }

        var output = (await stdout.ConfigureAwait(false)).Trim();
        var errors = (await stderr.ConfigureAwait(false)).Trim();
        log.Add("exit " + process.ExitCode + ": " + Tail(output) + (errors.Length > 0 ? " | " + Tail(errors) : string.Empty));
        if (process.ExitCode != 0)
        {
            return "The rescue helper failed: " + ReportedError(output, errors);
        }

        if (step.ExpectJsonOk && !ReportsOk(output))
        {
            return "The rescue helper did not confirm success: " + ReportedError(output, errors);
        }

        return null;
    }

    private static async Task<bool> RunActionAsync(
        RescueAction action,
        bool reopen,
        RescueSession session,
        IReadOnlyDictionary<string, string> values,
        IRescueHost? host,
        List<string> log,
        CancellationToken cancellationToken)
    {
        switch (action.Kind.ToLowerInvariant())
        {
            case "process":
                return await StopProcessesAsync(Expand(action.Target, values), action.WaitSeconds, log, cancellationToken).ConfigureAwait(false);
            case "url":
            {
                var url = Expand(action.Target, values);
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                log.Add("open: " + url);
                return true;
            }
            case "command":
            {
                var psi = new ProcessStartInfo { FileName = Expand(action.Target, values), UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in action.Args)
                {
                    psi.ArgumentList.Add(Expand(arg, values));
                }

                Process.Start(psi)?.Dispose();
                log.Add("start: " + psi.FileName);
                return true;
            }
            default:
                if (host is null)
                {
                    log.Add("no handler for " + action.Kind);
                    return false;
                }

                var ok = await host.RunNamedActionAsync(action.Kind, reopen, session, cancellationToken).ConfigureAwait(false);
                log.Add(action.Kind + (reopen ? " reopen: " : " stop: ") + (ok ? "done" : "failed"));
                return ok;
        }
    }

    private static async Task<bool> StopProcessesAsync(string imageName, int waitSeconds, List<string> log, CancellationToken cancellationToken)
    {
        var name = Path.GetFileNameWithoutExtension(imageName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var processes = Process.GetProcessesByName(name);
        foreach (var process in processes)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }

        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, waitSeconds));
        while (DateTime.UtcNow < deadline && processes.Any(p => !HasExited(p)))
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        var allGone = processes.All(HasExited);
        log.Add("stopped " + processes.Length + " " + name + " process(es)" + (allGone ? string.Empty : " (some still running)"));
        foreach (var process in processes)
        {
            process.Dispose();
        }

        return allGone;
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    internal static bool ReportsOk(string output)
    {
        var lastLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
        if (string.IsNullOrEmpty(lastLine))
        {
            return false;
        }

        try
        {
            return JsonNode.Parse(lastLine) is JsonObject obj && obj["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var b) && b;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ReportedError(string output, string errors)
    {
        var lastLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? string.Empty;
        try
        {
            if (JsonNode.Parse(lastLine) is JsonObject obj && obj["error"] is JsonNode error)
            {
                return error.ToString();
            }
        }
        catch (JsonException)
        {
        }

        return Tail(errors.Length > 0 ? errors : output);
    }

    private static string Tail(string text) => text.Length <= 400 ? text : "…" + text[^400..];

    private static IEnumerable<string> SafeDirs(string dir, string pattern)
    {
        try
        {
            return Directory.EnumerateDirectories(dir, pattern).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(dir, pattern).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}
