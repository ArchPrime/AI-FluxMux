using System;
using System.Collections.Generic;
using System.Linq;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Launch countdown follows a cold llama-server load, not a warm cached hit.
/// </summary>
public static class LocalLaunchCountdownEstimate
{
    public const int SampleLimit = 8;

    public readonly record struct Stats(int FastestMs, int MedianMs, int SlowestMs, int Count);

    public static Stats FromSamples(IEnumerable<int> samples)
    {
        var positive = (samples ?? Array.Empty<int>())
            .Where(ms => ms > 0)
            .OrderBy(ms => ms)
            .ToArray();
        if (positive.Length == 0)
        {
            return default;
        }

        return new Stats(
            FastestMs: positive[0],
            MedianMs: positive[positive.Length / 2],
            SlowestMs: positive[^1],
            Count: positive.Length);
    }

    public static int CountdownMs(Stats window, int persistedSlowestMs)
    {
        if (window.Count <= 0)
        {
            return Math.Max(0, persistedSlowestMs);
        }

        // The countdown follows the *slowest* observed load, not the median. The median tracks
        // the typical warm load (~20s), which is consistently wrong for cold loads (~120s).
        //
        // While the window is still filling up (fewer than SampleLimit samples), we keep the
        // persisted historical slowest if it is higher, because the window does not yet have
        // enough data to be trusted. Once the window is full, we trust the window's slowest:
        // a cold outlier from earlier has been pushed out, so the estimate naturally drops to
        // reflect the recent (warm) load times. This prevents the countdown from being stuck
        // at a historical worst-case value forever.
        if (persistedSlowestMs > window.SlowestMs && window.Count < SampleLimit)
        {
            return persistedSlowestMs;
        }

        return window.SlowestMs;
    }
}
