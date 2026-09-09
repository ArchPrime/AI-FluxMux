using System;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalVramPressureAdvisorTests
{
    [Fact]
    public void Assess_returns_no_warning_when_estimate_fits_with_headroom()
    {
        var assessment = LocalVramPressureAdvisor.Assess(
            footprintGiB: 10,
            freeGiB: 12,
            totalGiB: 24,
            offloadMode: "GPU only",
            imagesOn: false);

        Assert.False(assessment.ShowWarning);
        Assert.Equal(string.Empty, assessment.Message);
    }

    [Fact]
    public void Assess_gpu_only_warns_about_oom_without_spill()
    {
        var assessment = LocalVramPressureAdvisor.Assess(
            footprintGiB: 20,
            freeGiB: 8,
            totalGiB: 24,
            offloadMode: "GPU only",
            imagesOn: false);

        Assert.True(assessment.ShowWarning);
        Assert.Contains("GPU only", assessment.Message);
        Assert.Contains("cannot spill", assessment.Message);
        Assert.Contains("out-of-memory", assessment.Message);
        Assert.Contains("GPU & VRAM", assessment.Message);
    }

    [Fact]
    public void Assess_gpu_plus_cpu_warns_about_ram_spill_slowdown()
    {
        var assessment = LocalVramPressureAdvisor.Assess(
            footprintGiB: 20,
            freeGiB: 8,
            totalGiB: 24,
            offloadMode: "GPU + CPU",
            imagesOn: false);

        Assert.True(assessment.ShowWarning);
        Assert.Contains("GPU + CPU", assessment.Message);
        Assert.Contains("spill", assessment.Message);
        Assert.Contains("slower", assessment.Message);
    }

    [Fact]
    public void Assess_cpu_only_warns_about_system_ram_and_speed()
    {
        var assessment = LocalVramPressureAdvisor.Assess(
            footprintGiB: 12,
            freeGiB: 4,
            totalGiB: 24,
            offloadMode: "CPU only",
            imagesOn: false);

        Assert.True(assessment.ShowWarning);
        Assert.Contains("CPU only", assessment.Message);
        Assert.Contains("system RAM", assessment.Message);
        Assert.Contains("slower", assessment.Message);
    }

    [Fact]
    public void Assess_mentions_images_when_mmproj_is_on()
    {
        var assessment = LocalVramPressureAdvisor.Assess(
            footprintGiB: 20,
            freeGiB: 8,
            totalGiB: 24,
            offloadMode: "GPU only",
            imagesOn: true);

        Assert.True(assessment.ShowWarning);
        Assert.Contains("mmproj", assessment.Message);
    }

    [Fact]
    public void Assess_loaded_profile_with_measured_fit_skips_warning_even_when_free_is_low()
    {
        var assessment = LocalVramPressureAdvisor.Assess(
            footprintGiB: 17.6,
            freeGiB: 0.2,
            totalGiB: 31.8,
            offloadMode: "GPU + CPU",
            imagesOn: true,
            profileCurrentlyLoaded: true,
            measuredGiB: 17.4);

        Assert.False(assessment.ShowWarning);
    }

    [Fact]
    public void Assess_loaded_profile_without_measure_uses_total_capacity_not_free()
    {
        var assessment = LocalVramPressureAdvisor.Assess(
            footprintGiB: 17.6,
            freeGiB: 0.2,
            totalGiB: 31.8,
            offloadMode: "GPU + CPU",
            imagesOn: true,
            profileCurrentlyLoaded: true);

        Assert.False(assessment.ShowWarning);
    }

    [Fact]
    public void Assess_credits_vram_that_the_current_llama_server_will_release()
    {
        var assessment = LocalVramPressureAdvisor.Assess(
            footprintGiB: 27.5,
            freeGiB: 4.7,
            totalGiB: 31.8,
            offloadMode: "GPU only",
            imagesOn: true,
            reclaimableGiB: 26.8);

        Assert.False(assessment.ShowWarning);
        Assert.Equal(31.5, LocalVramPressureAdvisor.EffectiveFreeGiB(4.7, 31.8, 26.8), 1);
    }

    [Fact]
    public void Assess_still_warns_when_the_card_stays_short_after_unload()
    {
        var assessment = LocalVramPressureAdvisor.Assess(
            footprintGiB: 30,
            freeGiB: 2,
            totalGiB: 16,
            offloadMode: "GPU only",
            imagesOn: false,
            reclaimableGiB: 10);

        Assert.True(assessment.ShowWarning);
        Assert.Contains("after the current llama-server unloads", assessment.Message);
    }

    [Fact]
    public void Assess_cold_launch_still_warns_when_free_vram_is_low()
    {
        var assessment = LocalVramPressureAdvisor.Assess(
            footprintGiB: 20,
            freeGiB: 8,
            totalGiB: 24,
            offloadMode: "GPU + CPU",
            imagesOn: false);

        Assert.True(assessment.ShowWarning);
        Assert.Contains("only 8 GiB is free", assessment.Message);
    }

    [Fact]
    public void FormatMeasuredFootnote_includes_delta_and_timestamp()
    {
        var text = LocalVramPressureAdvisor.FormatMeasuredFootnote(14.2, new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));
        Assert.Contains("14.2", text);
        Assert.Contains("Measured", text);
    }
}
