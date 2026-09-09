using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class WindowPlacementTests
{
    [Fact]
    public void Saved_position_on_primary_is_kept()
    {
        var window = new ScreenBox(100, 100, 1100, 820);
        var screens = new[] { new ScreenBox(0, 0, 1920, 1040) };
        Assert.True(WindowPlacement.HasUsableOnScreenArea(window, screens));
    }

    [Fact]
    public void Saved_position_on_unplugged_monitor_is_rejected()
    {
        var window = new ScreenBox(3000, 100, 1100, 820);
        var screens = new[] { new ScreenBox(0, 0, 1920, 1040) };
        Assert.False(WindowPlacement.HasUsableOnScreenArea(window, screens));
    }
}