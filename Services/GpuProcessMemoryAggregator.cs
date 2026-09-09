using System.Collections.Generic;

namespace FluxMux.Avalonia.Services;

public static class GpuProcessMemoryAggregator
{
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
}
