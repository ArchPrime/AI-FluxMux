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
}
