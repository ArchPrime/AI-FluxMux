using System;
using System.Collections.Generic;

namespace FluxMux.Avalonia.Services;

public static class GpuProcessMemoryAggregator
{
    public const long DedicatedMemoryFloorBytes = 64L * 1024 * 1024;

    public static Dictionary<int, long> MergePerProcessAcrossGpus(IEnumerable<Dictionary<int, long>> perGpuPidBytes)
    {
        var merged = new Dictionary<int, long>();
        foreach (var perGpu in perGpuPidBytes)
        {
            foreach (var (pid, bytes) in perGpu)
            {
                merged[pid] = merged.GetValueOrDefault(pid) + bytes;
            }
        }

        return merged;
    }

    /// <summary>
    /// Motherboard graphics reports little or no dedicated memory. Keep only adapters
    /// with dedicated VRAM so the flyout lists what competes with llama-server.
    /// </summary>
    public static bool IsDedicatedAdapter(long dedicatedBytes, long sharedBytes)
    {
        _ = sharedBytes;
        return dedicatedBytes >= DedicatedMemoryFloorBytes;
    }

    public static string NormalizeGpuKey(string? instanceOrKey)
    {
        var value = (instanceOrKey ?? string.Empty).Trim();
        if (value.StartsWith("luid_", StringComparison.OrdinalIgnoreCase))
        {
            return value["luid_".Length..];
        }

        return value;
    }

    public static Dictionary<int, long> MergeDedicatedGpuProcesses(
        IReadOnlyDictionary<string, Dictionary<int, long>> bytesByGpuThenPid,
        IReadOnlyDictionary<string, (long DedicatedBytes, long SharedBytes)> adapterMemory)
    {
        var dedicated = new List<Dictionary<int, long>>();
        foreach (var (gpuKey, perPid) in bytesByGpuThenPid)
        {
            var normalized = NormalizeGpuKey(gpuKey);
            if (adapterMemory.TryGetValue(normalized, out var memory)
                && IsDedicatedAdapter(memory.DedicatedBytes, memory.SharedBytes))
            {
                dedicated.Add(perPid);
            }
        }

        if (dedicated.Count == 0)
        {
            return MergePerProcessAcrossGpus(bytesByGpuThenPid.Values);
        }

        return MergePerProcessAcrossGpus(dedicated);
    }

    /// <summary>
    /// Windows GPU Process Memory often omits CUDA compute apps. Same PID keeps the
    /// larger megabyte count; a PID only nvidia-smi names is added.
    /// </summary>
    public static Dictionary<int, (string Name, int Megabytes)> UnionPreferringLargerMegabytes(
        IEnumerable<(int Pid, string Name, int Megabytes)> windows,
        IEnumerable<(int Pid, string Name, int Megabytes)> computeApps)
    {
        var byPid = new Dictionary<int, (string Name, int Megabytes)>();
        foreach (var row in windows)
        {
            if (row.Pid <= 0 || row.Megabytes <= 0)
            {
                continue;
            }

            byPid[row.Pid] = (row.Name, row.Megabytes);
        }

        foreach (var row in computeApps)
        {
            if (row.Pid <= 0 || row.Megabytes <= 0)
            {
                continue;
            }

            if (!byPid.TryGetValue(row.Pid, out var existing))
            {
                byPid[row.Pid] = (row.Name, row.Megabytes);
                continue;
            }

            if (row.Megabytes > existing.Megabytes)
            {
                var name = string.IsNullOrWhiteSpace(existing.Name) ? row.Name : existing.Name;
                byPid[row.Pid] = (name, row.Megabytes);
            }
        }

        return byPid;
    }
}

