using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Runs a short console tool and returns its text. A missing executable
/// (nvidia-smi after an Intel-graphics boot) is empty output, not a crash.
/// </summary>
public static class ExternalProcessText
{
    public static string RunOrEmpty(string fileName, string arguments, int waitForExitMs = 5000)
    {
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
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(waitForExitMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(1000);
                }
                catch
                {
                }
            }

            return (stdout.GetAwaiter().GetResult() + Environment.NewLine + stderr.GetAwaiter().GetResult()).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }
}
