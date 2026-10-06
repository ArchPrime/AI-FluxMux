using System;
using System.Collections.Generic;
using System.IO;

namespace FluxMux.Updates.Core;

/// <summary>
/// Resolves the standalone FluxMux.Updates updater executable and builds the command-line
/// arguments for each subcommand from the app's current state. Kept in the shared core so
/// the app (and tests) can use the exact same resolution/arg-building the updater relies on.
/// </summary>
public static class UpdatePlan
{
    public const string UpdaterExeName = "FluxMux.Updates.exe";

    /// <summary>
    /// Finds the updater executable. In a published install it sits next to the running app;
    /// in a development build it is produced in the sibling FluxMux.Updates bin output.
    /// Returns null when it cannot be located.
    /// </summary>
    public static string? ResolveUpdaterExe(string appBaseDirectory)
    {
        var candidates = new List<string>();

        // 1. Next to the running app (published install).
        candidates.Add(Path.Combine(appBaseDirectory, UpdaterExeName));

        // 2. Development layout: the app runs from <repo>\FluxMux.Avalonia\bin\Debug\net10.0,
        //    and the updater (WinForms, net10.0-windows) builds to
        //    <repo>\FluxMux.Updates\bin\Debug\net10.0-windows.
        try
        {
            var appDir = new DirectoryInfo(appBaseDirectory);
            for (var i = 0; i < 6 && appDir is not null; i++)
            {
                candidates.Add(Path.Combine(appDir.FullName, "..", "..", "..", "..",
                    "FluxMux.Updates", "bin", "Debug", "net10.0-windows", UpdaterExeName));
                appDir = appDir.Parent;
            }
        }
        catch
        {
        }

        foreach (var candidate in candidates)
        {
            try
            {
                var full = Path.GetFullPath(candidate);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the argument array for the llama-server subcommand.
    /// </summary>
    public static IReadOnlyList<string> LlamaArgs(string serverDir)
    {
        var args = new List<string> { "llama", "--server-dir", serverDir };
        return args;
    }

    /// <summary>
    /// Builds the argument array for the AI-FluxMux subcommand.
    /// </summary>
    public static IReadOnlyList<string> FluxMuxArgs(string installDir, string feedUrl)
    {
        var args = new List<string> { "fluxmux", "--install-dir", installDir, "--feed-url", feedUrl };
        return args;
    }

    /// <summary>
    /// Builds the argument array for the DeepSeek Harness subcommand.
    /// </summary>
    public static IReadOnlyList<string> HarnessArgs(string? dshPath = null)
    {
        var args = new List<string> { "harness" };
        if (!string.IsNullOrWhiteSpace(dshPath))
        {
            args.Add("--dsh-path");
            args.Add(dshPath);
        }

        return args;
    }
}
