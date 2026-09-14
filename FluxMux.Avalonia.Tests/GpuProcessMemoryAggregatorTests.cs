using System;
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

    [Theory]
    [InlineData(307_015_680, 139_874_304, true)]
    [InlineData(0, 2_259_820_544, false)]
    [InlineData(8_192, 8_192, false)]
    public void IsDedicatedAdapter_keeps_dedicated_vram_and_drops_motherboard_graphics(
        long dedicated,
        long shared,
        bool expected)
    {
        Assert.Equal(expected, GpuProcessMemoryAggregator.IsDedicatedAdapter(dedicated, shared));
    }

    [Fact]
    public void MergeDedicatedGpuProcesses_omits_motherboard_graphics_clients()
    {
        var byGpu = new Dictionary<string, Dictionary<int, long>>(StringComparer.OrdinalIgnoreCase)
        {
            ["0x00000000_0xAAA_phys_0"] = new() { [11] = 400L * 1024 * 1024 },
            ["0x00000000_0xBBB_phys_0"] = new() { [22] = 50L * 1024 * 1024 }
        };
        var adapters = new Dictionary<string, (long DedicatedBytes, long SharedBytes)>(StringComparer.OrdinalIgnoreCase)
        {
            ["0x00000000_0xAAA_phys_0"] = (0, 2L * 1024 * 1024 * 1024),
            ["0x00000000_0xBBB_phys_0"] = (300L * 1024 * 1024, 100L * 1024 * 1024)
        };

        var merged = GpuProcessMemoryAggregator.MergeDedicatedGpuProcesses(byGpu, adapters);

        Assert.Single(merged);
        Assert.Equal(50L * 1024 * 1024, merged[22]);
        Assert.False(merged.ContainsKey(11));
    }

    [Fact]
    public void NormalizeGpuKey_strips_the_luid_prefix()
    {
        Assert.Equal(
            "0x00000000_0x00015689_phys_0",
            GpuProcessMemoryAggregator.NormalizeGpuKey("luid_0x00000000_0x00015689_phys_0"));
    }

    [Fact]
    public void UnionPreferringLargerMegabytes_adds_compute_apps_windows_did_not_name()
    {
        var merged = GpuProcessMemoryAggregator.UnionPreferringLargerMegabytes(
            [(11, "AI-FluxMux", 40)],
            [(22, "llama-server", 18000)]);

        Assert.Equal(40, merged[11].Megabytes);
        Assert.Equal("AI-FluxMux", merged[11].Name);
        Assert.Equal(18000, merged[22].Megabytes);
        Assert.Equal("llama-server", merged[22].Name);
    }

    [Fact]
    public void UnionPreferringLargerMegabytes_keeps_the_larger_count_for_the_same_pid()
    {
        var merged = GpuProcessMemoryAggregator.UnionPreferringLargerMegabytes(
            [(22, "llama-server", 12)],
            [(22, "llama-server", 18000)]);

        Assert.Equal(18000, Assert.Single(merged).Value.Megabytes);
    }
}
