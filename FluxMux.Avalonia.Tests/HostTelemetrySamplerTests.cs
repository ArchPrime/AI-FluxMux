using Avalonia.Media;
using FluxMux.Avalonia.Controls;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class HostTelemetrySamplerTests
{
    [Fact]
    public void ParseBestGpu_reads_vram_util_temp_and_power()
    {
        var parsed = HostTelemetrySampler.ParseBestGpu(
            "NVIDIA GeForce RTX 5090, 2252, 32607, 5, 51, 61.12");

        Assert.True(parsed.Available);
        Assert.Equal("NVIDIA GeForce RTX 5090", parsed.Name);
        Assert.Equal(2.2, parsed.UsedGiB);
        Assert.Equal(31.8, parsed.TotalGiB);
        Assert.Equal(5, parsed.UtilizationPercent);
        Assert.Equal(51, parsed.TemperatureC);
        Assert.Equal(61.12, parsed.PowerW);
    }

    [Fact]
    public void ParseBestGpu_picks_the_card_using_the_most_vram()
    {
        var parsed = HostTelemetrySampler.ParseBestGpu(
            "Intel Graphics, 400, 8192, 1, 40, [N/A]\nNVIDIA GeForce RTX 5090, 22528, 32607, 80, 62, 240.5");

        Assert.True(parsed.Available);
        Assert.Equal("NVIDIA GeForce RTX 5090", parsed.Name);
        Assert.Equal(22.0, parsed.UsedGiB);
        Assert.Equal(80, parsed.UtilizationPercent);
        Assert.Equal(240.5, parsed.PowerW);
    }

    [Fact]
    public void ParseBestGpu_treats_missing_nvidia_smi_as_unavailable()
    {
        var parsed = HostTelemetrySampler.ParseBestGpu("nvidia-smi is not recognized as an internal or external command");
        Assert.False(parsed.Available);
    }

    [Fact]
    public void Gauge_colors_run_green_to_red()
    {
        var green = SegmentedUtilizationGauge.ColorAt(0);
        var red = SegmentedUtilizationGauge.ColorAt(1);
        Assert.True(green.G > green.R);
        Assert.True(red.R > red.G);
    }

    [Fact]
    public void ComputeAvailablePhysicalRamGiB_does_not_report_low_when_cache_is_reclaimable()
    {
        // 32 GB machine, ~20 GB "used" in Task Manager. Strictly-free physical RAM
        // (ullAvailPhys) is only ~0.8 GiB because the rest is reclaimable file cache.
        // The old code returned ~0.8 GiB here and tripped the memory guard's false positive.
        const ulong totalPhys = 32UL * 1024 * 1024 * 1024;
        const ulong availPhys = 850UL * 1024 * 1024; // ~0.8 GiB strictly free
        const ulong availExtendedVirtual = 12UL * 1024 * 1024 * 1024; // ~12 GB truly committed

        double availableGiB = HostTelemetrySampler.ComputeAvailablePhysicalRamGiB(totalPhys, availPhys, availExtendedVirtual);

        // Available = total - committed = ~20 GiB (free + reclaimable cache), well above the
        // guard's 2 GiB floor, so no false positive.
        Assert.InRange(availableGiB, 19.0, 21.0);
        Assert.True(availableGiB >= 2.0, "available RAM must clear the 2 GiB hard-stop floor");
        Assert.True(availableGiB >= 4.0, "available RAM must clear the 4 GiB warning threshold");
    }

    [Fact]
    public void ComputeAvailablePhysicalRamGiB_reports_low_when_memory_is_actually_exhausted()
    {
        // 32 GB machine that is genuinely near OOM: ~30 GB committed, ~0.5 GiB free.
        const ulong totalPhys = 32UL * 1024 * 1024 * 1024;
        const ulong availPhys = 512UL * 1024 * 1024; // ~0.5 GiB strictly free
        const ulong availExtendedVirtual = 31UL * 1024 * 1024 * 1024; // ~31 GB committed

        double availableGiB = HostTelemetrySampler.ComputeAvailablePhysicalRamGiB(totalPhys, availPhys, availExtendedVirtual);

        // Available = total - committed = ~1 GiB, below the 2 GiB floor -> the guard SHOULD fire.
        Assert.InRange(availableGiB, 0.5, 1.5);
        Assert.True(availableGiB < 2.0, "genuinely low available RAM must stay below the 2 GiB floor");
    }

    [Fact]
    public void ComputeAvailablePhysicalRamGiB_never_reports_less_than_strictly_free()
    {
        // Defensive: even if the committed figure is anomalous, the result must not fall below
        // the strictly-free amount (which would be a regression to the old false-positive bug).
        const ulong totalPhys = 16UL * 1024 * 1024 * 1024;
        const ulong availPhys = 3UL * 1024 * 1024 * 1024; // 3 GiB strictly free
        const ulong availExtendedVirtual = 15UL * 1024 * 1024 * 1024; // would imply 1 GiB available

        double availableGiB = HostTelemetrySampler.ComputeAvailablePhysicalRamGiB(totalPhys, availPhys, availExtendedVirtual);

        Assert.True(availableGiB >= 3.0, "available must never be reported below the strictly-free amount");
    }

    [Fact]
    public void ComputeAvailablePhysicalRamGiB_returns_zero_when_total_is_zero()
    {
        Assert.Equal(0.0, HostTelemetrySampler.ComputeAvailablePhysicalRamGiB(0, 0, 0));
    }
}
