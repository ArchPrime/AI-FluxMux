using System;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class DisplayTopologyServiceTests
{
    private static MonitorPortRow Row(MonitorDriver driver, int index = 1, string name = "Generic PnP Monitor")
        => new()
        {
            MonitorIndex = index,
            MonitorName = name,
            DrivenBy = driver,
            DrivenByLabel = DisplayTopologyService.LabelForDriver(driver)
        };

    // ------------------------------------------------------------------
    // ClassifyDriver
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("Intel(R) Graphics", MonitorDriver.IntegratedGpu)]
    [InlineData("Intel(R) UHD Graphics 630", MonitorDriver.IntegratedGpu)]
    [InlineData("AMD Radeon(TM) Graphics", MonitorDriver.IntegratedGpu)]
    [InlineData("NVIDIA GeForce RTX 5090", MonitorDriver.DiscreteGpu)]
    [InlineData("NVIDIA GeForce RTX 4090", MonitorDriver.DiscreteGpu)]
    [InlineData("AMD Radeon RX 7900 XTX", MonitorDriver.DiscreteGpu)]
    [InlineData("Intel(R) Arc A770 Graphics", MonitorDriver.DiscreteGpu)]
    [InlineData("Microsoft Basic Display Adapter", MonitorDriver.Unknown)]
    [InlineData("", MonitorDriver.Unknown)]
    public void ClassifyDriver_maps_friendly_names_to_gpu_kind(string name, MonitorDriver expected)
    {
        Assert.Equal(expected, DisplayTopologyService.ClassifyDriver(name));
    }

    [Fact]
    public void ClassifyDriver_handles_null_name_as_unknown()
    {
        Assert.Equal(MonitorDriver.Unknown, DisplayTopologyService.ClassifyDriver(null));
    }

    // ------------------------------------------------------------------
    // LabelForDriver
    // ------------------------------------------------------------------

    [Fact]
    public void LabelForDriver_returns_stable_labels()
    {
        Assert.Equal(DisplayTopologyService.IgpuLabel, DisplayTopologyService.LabelForDriver(MonitorDriver.IntegratedGpu));
        Assert.Equal(DisplayTopologyService.DgpuLabel, DisplayTopologyService.LabelForDriver(MonitorDriver.DiscreteGpu));
        Assert.Equal(DisplayTopologyService.UnknownLabel, DisplayTopologyService.LabelForDriver(MonitorDriver.Unknown));
    }

    // ------------------------------------------------------------------
    // ClassifyTopology
    // ------------------------------------------------------------------

    [Fact]
    public void ClassifyTopology_all_on_igpu()
    {
        var monitors = new[]
        {
            Row(MonitorDriver.IntegratedGpu, 1),
            Row(MonitorDriver.IntegratedGpu, 2)
        };

        Assert.Equal(DisplayTopology.AllOnIgpu, DisplayTopologyService.ClassifyTopology(monitors));
    }

    [Fact]
    public void ClassifyTopology_all_on_dgpu()
    {
        var monitors = new[]
        {
            Row(MonitorDriver.DiscreteGpu, 1),
            Row(MonitorDriver.DiscreteGpu, 2)
        };

        Assert.Equal(DisplayTopology.AllOnDgpu, DisplayTopologyService.ClassifyTopology(monitors));
    }

    [Fact]
    public void ClassifyTopology_mixed_is_hybrid()
    {
        var monitors = new[]
        {
            Row(MonitorDriver.DiscreteGpu, 1),
            Row(MonitorDriver.IntegratedGpu, 2)
        };

        Assert.Equal(DisplayTopology.Hybrid, DisplayTopologyService.ClassifyTopology(monitors));
    }

    [Fact]
    public void ClassifyTopology_single_monitor_on_igpu()
    {
        var monitors = new[] { Row(MonitorDriver.IntegratedGpu, 1) };

        Assert.Equal(DisplayTopology.AllOnIgpu, DisplayTopologyService.ClassifyTopology(monitors));
    }

    [Fact]
    public void ClassifyTopology_no_monitors_is_unknown()
    {
        Assert.Equal(DisplayTopology.Unknown, DisplayTopologyService.ClassifyTopology(Array.Empty<MonitorPortRow>()));
    }

    [Fact]
    public void ClassifyTopology_all_unknown_drivers_is_unknown()
    {
        var monitors = new[]
        {
            Row(MonitorDriver.Unknown, 1),
            Row(MonitorDriver.Unknown, 2)
        };

        Assert.Equal(DisplayTopology.Unknown, DisplayTopologyService.ClassifyTopology(monitors));
    }

    [Fact]
    public void ClassifyTopology_unknown_plus_igpu_is_igpu()
    {
        // A monitor we cannot classify should not poison the topology of the ones we can.
        var monitors = new[]
        {
            Row(MonitorDriver.Unknown, 1),
            Row(MonitorDriver.IntegratedGpu, 2)
        };

        Assert.Equal(DisplayTopology.AllOnIgpu, DisplayTopologyService.ClassifyTopology(monitors));
    }
    // ------------------------------------------------------------------
    // BuildAdvice
    // ------------------------------------------------------------------

    [Fact]
    public void BuildAdvice_all_on_igpu_mentions_flexibility_and_vram()
    {
        var advice = DisplayTopologyService.BuildAdvice(
            DisplayTopology.AllOnIgpu,
            new[] { Row(MonitorDriver.IntegratedGpu, 1) });

        Assert.Contains("integrated GPU", advice);
        Assert.Contains("most flexible", advice);
        Assert.Contains("VRAM", advice);
    }

    [Fact]
    public void BuildAdvice_all_on_dgpu_keeps_app_toggles_but_notes_irreducible_overhead()
    {
        var advice = DisplayTopologyService.BuildAdvice(
            DisplayTopology.AllOnDgpu,
            new[] { Row(MonitorDriver.DiscreteGpu, 1) });

        // The corrected, accurate claim: app-level toggles still work.
        Assert.Contains("app-level toggles still work", advice);
        Assert.Contains("irreducible compositing overhead", advice);
        // The over-strong "toggles will not work" claim must NOT be present.
        Assert.DoesNotContain("the toggling options below will not work", advice);
        // The contradictory "nothing on the desktop can use the iGPU" claim must NOT be
        // present — it conflicts with "app-level toggles still work".
        Assert.DoesNotContain("neither the desktop nor anything running on it can use the iGPU", advice);
    }

    [Fact]
    public void BuildAdvice_hybrid_generates_per_monitor_sentences()
    {
        var monitors = new[]
        {
            Row(MonitorDriver.DiscreteGpu, 1),
            Row(MonitorDriver.IntegratedGpu, 2)
        };
        var advice = DisplayTopologyService.BuildAdvice(DisplayTopology.Hybrid, monitors);

        Assert.Contains("Monitor 1 is on the Discrete GPU.", advice);
        Assert.Contains("Monitor 2 is on the Motherboard GPU (iGPU).", advice);
        Assert.Contains("irreducible VRAM overhead", advice);
    }

    [Fact]
    public void BuildAdvice_unknown_says_could_not_detect()
    {
        var advice = DisplayTopologyService.BuildAdvice(DisplayTopology.Unknown, []);

        Assert.Contains("Could not detect which ports your monitors use", advice);
    }

    [Fact]
    public void BuildAdvice_is_null_safe()
    {
        var advice = DisplayTopologyService.BuildAdvice(DisplayTopology.Unknown, null);

        Assert.Equal(DisplayTopologyService.BuildAdvice(DisplayTopology.Unknown, []), advice);
    }

    // ------------------------------------------------------------------
    // SummarizeTopology
    // ------------------------------------------------------------------

    [Fact]
    public void SummarizeTopology_all_on_igpu()
    {
        var monitors = new[]
        {
            Row(MonitorDriver.IntegratedGpu, 1),
            Row(MonitorDriver.IntegratedGpu, 2)
        };

        Assert.Equal("2 monitor(s), all on the iGPU",
            DisplayTopologyService.SummarizeTopology(DisplayTopology.AllOnIgpu, monitors));
    }

    [Fact]
    public void SummarizeTopology_hybrid_counts_each_gpu()
    {
        var monitors = new[]
        {
            Row(MonitorDriver.DiscreteGpu, 1),
            Row(MonitorDriver.IntegratedGpu, 2),
            Row(MonitorDriver.IntegratedGpu, 3)
        };

        Assert.Equal("3 monitor(s): 2 on the iGPU, 1 on the Discrete GPU",
            DisplayTopologyService.SummarizeTopology(DisplayTopology.Hybrid, monitors));
    }

    [Fact]
    public void SummarizeTopology_no_monitors_is_empty()
    {
        Assert.Equal(string.Empty,
            DisplayTopologyService.SummarizeTopology(DisplayTopology.Unknown, []));
    }
}