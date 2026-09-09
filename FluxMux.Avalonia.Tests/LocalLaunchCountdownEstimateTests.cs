using System.Linq;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalLaunchCountdownEstimateTests
{
    [Fact]
    public void Countdown_uses_the_slower_sample_not_the_warm_hit()
    {
        var window = LocalLaunchCountdownEstimate.FromSamples(new[] { 13574, 10254 });

        Assert.Equal(10254, window.FastestMs);
        Assert.Equal(13574, window.SlowestMs);
        Assert.Equal(13574, LocalLaunchCountdownEstimate.CountdownMs(window, persistedSlowestMs: 0));
    }

    [Fact]
    public void Countdown_keeps_a_colder_mark_until_the_sample_window_fills_with_faster_launches()
    {
        var window = LocalLaunchCountdownEstimate.FromSamples(new[] { 10000, 10100, 9900 });

        Assert.Equal(17265, LocalLaunchCountdownEstimate.CountdownMs(window, persistedSlowestMs: 17265));
    }

    [Fact]
    public void Countdown_drops_the_old_cold_mark_after_a_full_window_of_faster_launches()
    {
        var warms = Enumerable.Repeat(10000, LocalLaunchCountdownEstimate.SampleLimit);
        var window = LocalLaunchCountdownEstimate.FromSamples(warms);

        Assert.Equal(10000, LocalLaunchCountdownEstimate.CountdownMs(window, persistedSlowestMs: 17265));
    }

    [Fact]
    public void Empty_samples_keep_the_persisted_slowest()
    {
        Assert.Equal(12000, LocalLaunchCountdownEstimate.CountdownMs(default, persistedSlowestMs: 12000));
        Assert.Equal(0, LocalLaunchCountdownEstimate.CountdownMs(default, persistedSlowestMs: 0));
    }
}
