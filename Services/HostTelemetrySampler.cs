using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32;

namespace FluxMux.Avalonia.Services;

public readonly record struct HostTelemetrySnapshot(
    bool GpuAvailable,
    string GpuName,
    double GpuVramUsedGiB,
    double GpuVramTotalGiB,
    double GpuUtilizationPercent,
    double? GpuTemperatureC,
    double? GpuPowerW,
    double RamUsedGiB,
    double RamTotalGiB,
    double RamPercent,
    double CpuPercent,
    int CpuCores,
    double? CpuGhz);

public sealed class HostTelemetrySampler
{
    private const double BytesPerGiB = 1073741824.0;
    private ulong _prevIdle;
    private ulong _prevKernel;
    private ulong _prevUser;
    private bool _hasCpuBaseline;
    private double _lastCpuPercent;
    private readonly object _cpuLock = new();

    public HostTelemetrySnapshot Sample()
    {
        var gpu = SampleGpu();
        var ram = SampleRam();
        var cpu = SampleCpu();
        return new HostTelemetrySnapshot(
            gpu.Available,
            gpu.Name,
            gpu.UsedGiB,
            gpu.TotalGiB,
            gpu.UtilizationPercent,
            gpu.TemperatureC,
            gpu.PowerW,
            ram.UsedGiB,
            ram.TotalGiB,
            ram.Percent,
            cpu,
            Math.Max(1, Environment.ProcessorCount),
            ReadNominalCpuGhz());
    }

    public static (bool Available, string Name, double UsedGiB, double TotalGiB, double UtilizationPercent, double? TemperatureC, double? PowerW)
        ParseBestGpu(string csv)
    {
        var best = (Available: false, Name: string.Empty, UsedGiB: 0.0, TotalGiB: 0.0, UtilizationPercent: 0.0, TemperatureC: (double?)null, PowerW: (double?)null);
        if (string.IsNullOrWhiteSpace(csv)
            || csv.Contains("NVIDIA-SMI has failed", StringComparison.OrdinalIgnoreCase)
            || csv.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || csv.Contains("is not recognized", StringComparison.OrdinalIgnoreCase)
            || csv.Contains("No such file", StringComparison.OrdinalIgnoreCase))
        {
            return best;
        }

        foreach (var rawLine in csv.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = rawLine.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 3)
            {
                continue;
            }

            var name = parts[0];
            var usedMiB = ParseMetric(parts[1]);
            var totalMiB = ParseMetric(parts[2]);
            if (usedMiB is null || totalMiB is null || totalMiB <= 0)
            {
                continue;
            }

            var usedGiB = Math.Round(usedMiB.Value / 1024.0, 1);
            var totalGiB = Math.Round(totalMiB.Value / 1024.0, 1);
            if (!best.Available || usedGiB > best.UsedGiB)
            {
                best = (
                    true,
                    name,
                    usedGiB,
                    totalGiB,
                    Math.Clamp(ParseMetric(parts.Length > 3 ? parts[3] : null) ?? 0, 0, 100),
                    ParseMetric(parts.Length > 4 ? parts[4] : null),
                    ParseMetric(parts.Length > 5 ? parts[5] : null));
            }
        }

        return best;
    }

    private static (bool Available, string Name, double UsedGiB, double TotalGiB, double UtilizationPercent, double? TemperatureC, double? PowerW) SampleGpu()
    {
        var csv = RunTimedCommand(
            "nvidia-smi",
            "--query-gpu=name,memory.used,memory.total,utilization.gpu,temperature.gpu,power.draw --format=csv,noheader,nounits",
            1500);
        var parsed = ParseBestGpu(csv);
        if (parsed.Available)
        {
            return parsed;
        }

        csv = RunTimedCommand(
            "nvidia-smi",
            "--query-gpu=name,memory.used,memory.total,utilization.gpu --format=csv,noheader,nounits",
            1500);
        return ParseBestGpu(csv);
    }

    private static (double UsedGiB, double TotalGiB, double Percent) SampleRam()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0)
        {
            return (0, 0, 0);
        }

        var used = status.ullTotalPhys - status.ullAvailPhys;
        return (
            Math.Round(used / BytesPerGiB, 1),
            Math.Round(status.ullTotalPhys / BytesPerGiB, 1),
            Math.Clamp(status.dwMemoryLoad, 0, 100));
    }

    private double SampleCpu()
    {
        lock (_cpuLock)
        {
            if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
            {
                return _lastCpuPercent;
            }

            var idle = FileTimeToUInt64(idleTime);
            var kernel = FileTimeToUInt64(kernelTime);
            var user = FileTimeToUInt64(userTime);
            if (!_hasCpuBaseline)
            {
                _prevIdle = idle;
                _prevKernel = kernel;
                _prevUser = user;
                _hasCpuBaseline = true;
                return _lastCpuPercent;
            }

            var idleDelta = idle - _prevIdle;
            var kernelDelta = kernel - _prevKernel;
            var userDelta = user - _prevUser;
            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;

            var total = kernelDelta + userDelta;
            if (total == 0)
            {
                return _lastCpuPercent;
            }

            var busy = total > idleDelta ? total - idleDelta : 0;
            _lastCpuPercent = Math.Clamp(100.0 * busy / total, 0, 100);
            return _lastCpuPercent;
        }
    }

    private static double? ReadNominalCpuGhz()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("~MHz") is int mhz && mhz > 0)
            {
                return Math.Round(mhz / 1000.0, 1);
            }

            if (key?.GetValue("~MHz") is uint umhz && umhz > 0)
            {
                return Math.Round(umhz / 1000.0, 1);
            }
        }
        catch
        {
        }

        return null;
    }

    private static double? ParseMetric(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)
            || text.Contains("N/A", StringComparison.OrdinalIgnoreCase)
            || text.Contains("[N/A]", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var trimmed = text.Trim().TrimEnd('%', 'W', 'C', 'c');
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
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

    private static ulong FileTimeToUInt64(FILETIME time)
        => unchecked(((ulong)(uint)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
