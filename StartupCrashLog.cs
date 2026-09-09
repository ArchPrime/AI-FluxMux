using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FluxMux.Avalonia.Services;

namespace FluxMux.Avalonia;

internal static class StartupCrashLog
{
    internal const string SidecarFileName = "AI-FluxMux-could-not-start.txt";

    private static volatile bool _windowShown;

    /// <summary>
    /// Called once the main window is on screen. After that a crash is not a startup
    /// failure, and telling the operator to rebuild sends them after the wrong thing.
    /// </summary>
    internal static void MarkWindowShown() => _windowShown = true;

    internal static void ReportMissingCompiledXaml()
    {
        Report(
            new InvalidOperationException("Compiled Avalonia XAML marker XamlIlPopulateTrampoline was not found in this assembly."),
            missingCompiledXaml: true);
    }

    internal static void Report(Exception exception)
    {
        Report(exception, missingCompiledXaml: LooksLikeMissingCompiledXaml(exception));
    }

    private static void Report(Exception exception, bool missingCompiledXaml)
    {
        var afterStartup = _windowShown && !missingCompiledXaml;
        var recovery = BuildRecoveryText(missingCompiledXaml, afterStartup);
        var logPath = WriteLog(recovery, exception);
        var sidecarPath = WriteSidecar(recovery, logPath);
        ShowError(DialogTitle(afterStartup), BuildDialogText(recovery, logPath, sidecarPath));
    }

    internal static string DialogTitle(bool afterStartup)
        => afterStartup ? "AI-FluxMux stopped" : "AI-FluxMux could not start";

    internal static string BuildRecoveryText(bool missingCompiledXaml, bool afterStartup = false)
    {
        // A crash after the window was up is not a broken build, so rebuilding is not
        // the recovery and saying so only sends the operator somewhere unhelpful.
        if (afterStartup)
        {
            return "AI-FluxMux was running and stopped unexpectedly. Nothing it had already saved is lost."
                + Environment.NewLine
                + Environment.NewLine
                + "Recover:"
                + Environment.NewLine
                + "1. Close this message."
                + Environment.NewLine
                + "2. Start AI-FluxMux again the same way you usually do."
                + Environment.NewLine
                + "3. If a local model was loaded, load it again from Quick Select."
                + Environment.NewLine
                + Environment.NewLine
                + "If this keeps happening, the technical details below say what failed.";
        }

        var reason = missingCompiledXaml
            ? "AI-FluxMux opened, then closed before a window appeared. This copy of the app is incomplete (a test build while AI-FluxMux was already running can do that)."
            : "AI-FluxMux could not start.";

        return reason
            + Environment.NewLine
            + Environment.NewLine
            + "Recover:"
            + Environment.NewLine
            + "1. Close this message."
            + Environment.NewLine
            + "2. Open a command prompt in the AI-FluxMux folder (the folder that contains FluxMux.Avalonia.csproj)."
            + Environment.NewLine
            + "3. Run:  dotnet build"
            + Environment.NewLine
            + "4. Start AI-FluxMux again the same way you usually do."
            + Environment.NewLine
            + Environment.NewLine
            + "Do not start a copy from an artifacts folder.";
    }

    private static bool LooksLikeMissingCompiledXaml(Exception exception)
    {
        var message = exception.ToString();
        return message.Contains("precompiled XAML", StringComparison.OrdinalIgnoreCase)
            || message.Contains("XamlIlPopulateTrampoline", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildDialogText(string recovery, string logPath, string sidecarPath)
    {
        var text = recovery;
        if (!string.IsNullOrWhiteSpace(sidecarPath))
        {
            text += Environment.NewLine + Environment.NewLine + "These steps were also saved next to the program:"
                + Environment.NewLine + sidecarPath;
        }

        if (!string.IsNullOrWhiteSpace(logPath))
        {
            text += Environment.NewLine + Environment.NewLine + "Technical details:"
                + Environment.NewLine + logPath;
        }

        return text;
    }

    private static string WriteLog(string recovery, Exception exception)
    {
        var body = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            + Environment.NewLine
            + recovery
            + Environment.NewLine
            + Environment.NewLine
            + exception
            + Environment.NewLine;
        foreach (var path in LogPathsToTry())
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(path, body, Encoding.UTF8);
                return path;
            }
            catch
            {
                // Try the next location. An installed AI-FluxMux cannot write beside
                // its own exe, and this note is most needed exactly when it will not start.
            }
        }

        return string.Empty;
    }

    private static IEnumerable<string> LogPathsToTry()
    {
        yield return ResolveLogPath();
        yield return Path.Combine(AppContext.BaseDirectory, "FluxMux_Startup_Error.log");
        yield return Path.Combine(UserDirectory(), "Logs", "FluxMux_Startup_Error.log");
    }

    private static string UserDirectory()
    {
        try
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                FluxMuxConfigPaths.AppFolderName);
        }
        catch
        {
            return AppContext.BaseDirectory;
        }
    }

    private static string WriteSidecar(string recovery, string logPath)
    {
        var body = recovery;
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            body += Environment.NewLine + Environment.NewLine + "Technical details: " + logPath;
        }

        body += Environment.NewLine;
        string[] paths =
        [
            Path.Combine(AppContext.BaseDirectory, SidecarFileName),
            Path.Combine(UserDirectory(), SidecarFileName)
        ];

        foreach (var path in paths)
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(path, body, Encoding.UTF8);
                return path;
            }
            catch
            {
                // Same reason as the log: the install folder may be read-only.
            }
        }

        return string.Empty;
    }

    private static string ResolveLogPath()
    {
        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var configPath = Path.Combine(current, ".vscode", "fluxmux_config.json");
            if (File.Exists(configPath))
            {
                // current holds .vscode, so it is the workspace root itself. One step up
                // is the workbench root, where FluxMuxRuntimeService keeps Logs. Taking
                // two steps landed this file on the drive root.
                var workbenchRoot = Directory.GetParent(current)?.FullName ?? current;
                return Path.Combine(workbenchRoot, "Logs", "FluxMux_Startup_Error.log");
            }

            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            current = parent.FullName;
        }

        return Path.Combine(AppContext.BaseDirectory, "FluxMux_Startup_Error.log");
    }

    internal static void Notify(string title, string text)
    {
        ShowMessage(title, text, 0x00000040);
    }

    private static void ShowError(string title, string text)
    {
        ShowMessage(title, text, 0x00000010);
    }

    private static void ShowMessage(string title, string text, uint type)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        MessageBoxW(IntPtr.Zero, text, title, type);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);
}