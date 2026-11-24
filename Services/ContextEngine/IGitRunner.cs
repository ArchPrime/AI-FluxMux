using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluxMux.Avalonia.Services.ContextEngine;

/// <summary>
/// The git execution seam for the 📌 Checkpoint button (FluxMuxRuntimeService.RunGitCheckpointAsync).
/// Keeps git behavior testable (a fake runner in unit tests) and portable (a different runner on
/// another platform).
/// </summary>
public interface IGitRunner
{
    /// <summary>
    /// Creates a git checkpoint (a commit) at <paramref name="workDir"/> with the given message.
    /// Returns <c>(ok, message)</c>. Never throws; on failure <c>ok</c> is false and
    /// <c>message</c> carries the error.
    /// </summary>
    (bool Ok, string Message) CreateCheckpoint(string workDir, string message);
}

/// <summary>
/// The default <see cref="IGitRunner"/>: shells out to <c>git</c> via <c>System.Diagnostics</c>.
/// Used in production; tests inject a fake.
/// </summary>
public sealed class GitRunner : IGitRunner
{
    public (bool Ok, string Message) CreateCheckpoint(string workDir, string message)
    {
        try
        {
            // Include the folder name in the commit message so we can filter checkpoints
            // by project when multiple projects share the same git repo.
            var folderName = System.IO.Path.GetFileName(workDir.TrimEnd(System.IO.Path.DirectorySeparatorChar));
            var fullMessage = $"fluxmux-checkpoint [{folderName}]: {message}";

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("add");
            psi.ArgumentList.Add("-A");
            var add = System.Diagnostics.Process.Start(psi);
            add?.WaitForExit();

            var psi2 = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi2.ArgumentList.Add("commit");
            psi2.ArgumentList.Add("-m");
            psi2.ArgumentList.Add(fullMessage);
            psi2.ArgumentList.Add("--allow-empty");
            var commit = System.Diagnostics.Process.Start(psi2);
            commit?.WaitForExit();

            var exit = commit?.ExitCode ?? -1;
            var err = commit?.StandardError.ReadToEnd() ?? string.Empty;
            return exit == 0
                ? (true, $"checkpoint created in {workDir}")
                : (false, string.IsNullOrWhiteSpace(err) ? "git commit failed" : err.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}