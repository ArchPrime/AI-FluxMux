using System;
using System.IO;
using System.Text;

namespace FluxMux.Avalonia;

/// <summary>
/// Writes unhandled-exception details to a file on the user's Desktop so a crash
/// can be diagnosed without a debugger. One file per crash, timestamped.
/// </summary>
internal static class CrashLogger
{
    private static readonly object _lock = new();

    public static void LogUnhandled(Exception exception, string source)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (string.IsNullOrWhiteSpace(desktop))
            {
                desktop = AppContext.BaseDirectory;
            }

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var path = Path.Combine(desktop, $"FluxMux_Crash_{stamp}.txt");

            var sb = new StringBuilder();
            sb.AppendLine("AI-FluxMux crash report");
            sb.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"Source: {source}");
            sb.AppendLine($"Process: {Environment.ProcessId}");
            sb.AppendLine($"OS: {Environment.OSVersion}");
            sb.AppendLine($"Runtime: {Environment.Version}");
            sb.AppendLine();
            sb.AppendLine("=== Exception ===");
            sb.AppendLine(exception.ToString());
            sb.AppendLine();
            sb.AppendLine("=== Stack Trace ===");
            sb.AppendLine(exception.StackTrace ?? "(no stack trace)");

            // Include inner exceptions
            var inner = exception.InnerException;
            var depth = 0;
            while (inner is not null && depth < 10)
            {
                sb.AppendLine();
                sb.AppendLine($"=== Inner Exception ({depth + 1}) ===");
                sb.AppendLine(inner.ToString());
                sb.AppendLine("Stack: " + (inner.StackTrace ?? "(none)"));
                inner = inner.InnerException;
                depth++;
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Last-resort: try temp
            try
            {
                var tmp = Path.Combine(Path.GetTempPath(), $"FluxMux_Crash_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                File.WriteAllText(tmp, exception.ToString(), Encoding.UTF8);
            }
            catch
            {
                // nothing else we can do
            }
        }
    }
}