using System;
using System.Globalization;

namespace FluxMux.Avalonia.Services;

public static class LocalGpuVramSample
{
    public static bool TryRead(out double usedGiB, out double totalGiB, out double freeGiB)
    {
        usedGiB = 0;
        totalGiB = 0;
        freeGiB = 0;
        var csv = RunTimedCommand(
            "nvidia-smi",
            "--query-gpu=memory.used,memory.total,memory.free --format=csv,noheader,nounits",
            1500);
        if (string.IsNullOrWhiteSpace(csv))
        {
            return false;
        }

        var bestUsed = -1d;
        foreach (var rawLine in csv.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = rawLine.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 3)
            {
                continue;
            }

            var usedMiB = ParseMetric(parts[0]);
            var totalMiB = ParseMetric(parts[1]);
            var freeMiB = ParseMetric(parts[2]);
            if (usedMiB is null || totalMiB is null || totalMiB <= 0)
            {
                continue;
            }

            if (bestUsed < 0 || usedMiB.Value > bestUsed)
            {
                bestUsed = usedMiB.Value;
                usedGiB = Math.Round(usedMiB.Value / 1024.0, 2);
                totalGiB = Math.Round(totalMiB.Value / 1024.0, 2);
                freeGiB = Math.Round((freeMiB ?? Math.Max(0, totalMiB.Value - usedMiB.Value)) / 1024.0, 2);
            }
        }

        return bestUsed >= 0;
    }

    private static double? ParseMetric(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)
            || text.Contains("N/A", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string RunTimedCommand(string fileName, string arguments, int timeoutMs)
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
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
            if (!process.WaitForExit(timeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return string.Empty;
            }

            return (process.StandardOutput.ReadToEnd() + Environment.NewLine + process.StandardError.ReadToEnd()).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }
}
