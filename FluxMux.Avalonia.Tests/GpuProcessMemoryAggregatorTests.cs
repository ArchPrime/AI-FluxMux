using System.Collections.Generic;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class GpuProcessMemoryAggregatorTests
{
    [Fact]
    public void MergePerProcessAcrossGpus_sums_the_same_pid_on_multiple_cards()
    {
        var merged = GpuProcessMemoryAggregator.MergePerProcessAcrossGpus(
        [
            new Dictionary<int, long> { [100] = 512L * 1024 * 1024, [200] = 128L * 1024 * 1024 },
            new Dictionary<int, long> { [100] = 256L * 1024 * 1024, [300] = 64L * 1024 * 1024 }
        ]);

        Assert.Equal(3, merged.Count);
        Assert.Equal(768L * 1024 * 1024, merged[100]);
        Assert.Equal(128L * 1024 * 1024, merged[200]);
        Assert.Equal(64L * 1024 * 1024, merged[300]);
    }
}
