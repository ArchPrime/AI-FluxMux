using System;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalVramFootprintEstimateTests
{
    [Fact]
    public void Unknown_gguf_stays_at_the_conservative_ceiling()
    {
        Assert.Equal(131072, LocalVramFootprintEstimate.AdviseModelMaxContext(0));
    }

    [Fact]
    public void Short_context_gguf_keeps_the_file_window()
    {
        Assert.Equal(32768, LocalVramFootprintEstimate.AdviseModelMaxContext(32768));
    }

    [Fact]
    public void Long_context_gguf_uses_the_editor_max_as_a_cap_only()
    {
        Assert.Equal(
            DeepSeekHarnessSetup.MaxLocalContextWindow,
            LocalVramFootprintEstimate.AdviseModelMaxContext(131072));
    }

    [Fact]
    public void Saturated_editor_max_uses_leftover_vram_not_a_percent_of_262k()
    {
        var on32 = LocalVramFootprintEstimate.SoftenSaturatedContext(
            DeepSeekHarnessSetup.MaxLocalContextWindow,
            DeepSeekHarnessSetup.MaxLocalContextWindow,
            leftoverGpuGiB: LocalVramFootprintEstimate.TextWorkingLeftoverGiB);
        var on16 = LocalVramFootprintEstimate.SoftenSaturatedContext(
            DeepSeekHarnessSetup.MaxLocalContextWindow,
            DeepSeekHarnessSetup.MaxLocalContextWindow,
            leftoverGpuGiB: 6);
        Assert.Equal(LocalVramFootprintEstimate.TextWorkingContext, on32);
        Assert.True(on16 < on32);
        Assert.True(on16 < 120000);
    }

    [Fact]
    public void Vision_reserve_grows_with_max_image_edge()
    {
        var at672 = LocalVramFootprintEstimate.VisionReserveGiB(0.9, 672);
        var at1344 = LocalVramFootprintEstimate.VisionReserveGiB(0.9, 1344);
        var at2048 = LocalVramFootprintEstimate.VisionReserveGiB(0.9, 2048);
        Assert.True(at672 < at1344);
        Assert.True(at1344 < at2048);
        Assert.InRange(at1344, 2.7, 2.9);
    }

    [Fact]
    public void Vision_haircut_is_smaller_on_a_larger_leftover_gpu()
    {
        var reserve = LocalVramFootprintEstimate.VisionReserveGiB(0.9, 1344);
        var on32 = LocalVramFootprintEstimate.ApplyVisionContextHaircut(212992, leftoverGpuGiB: 12, reserve);
        var on16 = LocalVramFootprintEstimate.ApplyVisionContextHaircut(98304, leftoverGpuGiB: 5, reserve);
        Assert.True(on32 > on16);
        Assert.True(on32 < 212992);
        Assert.True(on16 < 98304);
    }

    [Fact]
    public void Vision_haircut_at_text_working_leftover_is_the_images_working_context()
    {
        var reserve = LocalVramFootprintEstimate.VisionReserveGiB(0.9, 1344);
        var advised = LocalVramFootprintEstimate.ApplyVisionContextHaircut(
            LocalVramFootprintEstimate.TextWorkingContext,
            LocalVramFootprintEstimate.TextWorkingLeftoverGiB,
            reserve);
        Assert.Equal(LocalVramFootprintEstimate.ImagesWorkingContext, advised);
    }

    [Fact]
    public void Images_headroom_note_says_the_wizard_does_not_change_images()
    {
        var on = LocalVramFootprintEstimate.FormatImagesHeadroomNote(true, 2.8, 1344, 32);
        var off = LocalVramFootprintEstimate.FormatImagesHeadroomNote(false, 0, 1344, 32);
        Assert.Contains("does not change Images", on, StringComparison.Ordinal);
        Assert.Contains("does not change Images", off, StringComparison.Ordinal);
        Assert.Contains("1344", on, StringComparison.Ordinal);
        Assert.Contains("32 GiB GPU", on, StringComparison.Ordinal);
    }
}
