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

        if (persistedSlowestMs > window.SlowestMs && window.Count < SampleLimit)
        {
            return persistedSlowestMs;
        }

        return window.SlowestMs;
    }
}
