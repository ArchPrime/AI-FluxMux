using System.Linq;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalLaunchCountdownEstimateTests
{
    [Fact]
    public void Countdown_uses_the_slowest_of_the_recent_window()
    {
        var window = LocalLaunchCountdownEstimate.FromSamples(new[] { 13574, 10254 });

        Assert.Equal(10254, window.FastestMs);
        Assert.Equal(13574, window.SlowestMs);
        // Two samples: slowest is the max = 13574.
        Assert.Equal(13574, LocalLaunchCountdownEstimate.CountdownMs(window, persistedSlowestMs: 0));
    }

    [Fact]
    public void Countdown_uses_the_max_of_window_slowest_and_persisted()
    {
        // The countdown is the MAXIMUM of the recent window's slowest and the persisted
        // historical slowest. This ensures a cold load from earlier is not forgotten when
        // recent warm loads are fast.
        var window = LocalLaunchCountdownEstimate.FromSamples(new[] { 10000, 10100, 9900 });

        // Window slowest = 10100, persisted = 17265 → countdown = 17265.
        Assert.Equal(17265, LocalLaunchCountdownEstimate.CountdownMs(window, persistedSlowestMs: 17265));
    }

    [Fact]
    public void Countdown_rises_when_window_slowest_exceeds_persisted()
    {
        // If the recent window has a slower load than the persisted value, the countdown
        // rises to match it.
        var window = LocalLaunchCountdownEstimate.FromSamples(new[] { 20000, 25000, 18000 });

        // Window slowest = 25000, persisted = 17265 → countdown = 25000.
        Assert.Equal(25000, LocalLaunchCountdownEstimate.CountdownMs(window, persistedSlowestMs: 17265));
    }

    [Fact]
    public void Countdown_includes_the_cold_start_outlier()
    {
        // A one-off 99s cold-start outlier IS included in the estimate because the
        // slowest of the window is used. This is intentional: if the model just did a
        // cold load, the next launch is likely to be similar (cold cache). The outlier
        // gets pushed out after SampleLimit (8) subsequent launches.
        var window = LocalLaunchCountdownEstimate.FromSamples(new[] { 99136, 20093, 18021, 18113, 18210 });

        // Sorted: 18021, 18113, 18210, 20093, 99136 → slowest = 99136.
        Assert.Equal(99136, LocalLaunchCountdownEstimate.CountdownMs(window, persistedSlowestMs: 99136));
    }

    [Fact]
    public void Countdown_drops_to_window_slowest_once_window_is_full()
    {
        // Once the window is full (SampleLimit samples), the persisted historical slowest is
        // no longer kept: the cold outlier has been pushed out, so the estimate drops to the
        // window's slowest. This prevents the countdown from being stuck at a historical
        // worst-case value forever.
        var warms = Enumerable.Repeat(10000, LocalLaunchCountdownEstimate.SampleLimit);
        var window = LocalLaunchCountdownEstimate.FromSamples(warms);

        // Window is full (8 samples), window slowest = 10000, persisted = 17265.
        // Because the window is full, the countdown drops to the window slowest (10000).
        Assert.Equal(10000, LocalLaunchCountdownEstimate.CountdownMs(window, persistedSlowestMs: 17265));
    }

    [Fact]
    public void Countdown_keeps_persisted_slowest_while_window_is_still_filling()
    {
        // While the window is still filling up (fewer than SampleLimit samples), the persisted
        // historical slowest is kept if it is higher, because the window does not yet have
        // enough data to be trusted.
        var warms = Enumerable.Repeat(10000, LocalLaunchCountdownEstimate.SampleLimit - 1);
        var window = LocalLaunchCountdownEstimate.FromSamples(warms);

        // Window has 7 samples (not full), window slowest = 10000, persisted = 17265.
        // Because the window is not full, the countdown keeps the persisted slowest (17265).
        Assert.Equal(17265, LocalLaunchCountdownEstimate.CountdownMs(window, persistedSlowestMs: 17265));
    }

    [Fact]
    public void Empty_samples_keep_the_persisted_slowest()
    {
        Assert.Equal(12000, LocalLaunchCountdownEstimate.CountdownMs(default, persistedSlowestMs: 12000));
        Assert.Equal(0, LocalLaunchCountdownEstimate.CountdownMs(default, persistedSlowestMs: 0));
    }
}
