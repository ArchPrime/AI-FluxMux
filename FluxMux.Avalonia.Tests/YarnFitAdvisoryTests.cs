using FluxMux.Avalonia.ViewModels;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>The YaRN "may not fit" advisory speaks only about the selected size.</summary>
public sealed class YarnFitAdvisoryTests
{
    private const int Native = 262_144;
    private const int FitsAbout620K = 620 * 1024;

    [Theory]
    [InlineData("Auto")]
    [InlineData("")]
    [InlineData(null)]
    public void Auto_or_nothing_selected_shows_no_warning(string? selected)
    {
        Assert.Equal(string.Empty, MainViewModel.BuildYarnFitAdvisory(selected, Native, FitsAbout620K));
    }

    [Fact]
    public void A_selected_size_that_fits_shows_no_warning()
    {
        Assert.Equal(string.Empty, MainViewModel.BuildYarnFitAdvisory("512K", Native, FitsAbout620K));
    }

    [Fact]
    public void A_selected_size_that_does_not_fit_is_named_with_what_fits()
    {
        var text = MainViewModel.BuildYarnFitAdvisory("768K", Native, FitsAbout620K);

        Assert.StartsWith("⚠ 768K may not fit on this PC", text);
        Assert.Contains("about 620K fits in memory", text);
        Assert.DoesNotContain("1M", text); // not about sizes that were not selected
    }

    [Fact]
    public void A_size_beyond_physical_memory_says_it_cannot_fit()
    {
        var text = MainViewModel.BuildYarnFitAdvisory("1M", Native, FitsAbout620K, physicalCeiling: 900 * 1024);

        Assert.StartsWith("⚠ 1M is larger than this PC's memory can hold", text);
        Assert.Contains("choose a smaller size or Auto", text);
    }
}
