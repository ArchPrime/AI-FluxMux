using System;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// One settle-and-retry when llama-server dies during startup after a kill.
/// The daemon port can look free while CUDA still holds VRAM. Do not AutoStart
/// on app open, and do not retry a second time.
/// </summary>
public static class LocalLaunchStartupRetry
{
    public const int MaxAttempts = 2;
    public const int PortWaitMs = 15000;
    public const int VramSettleMs = 20000;
    public const int VramPollMs = 1000;
    public const double VramFreeGainGiB = 1.0;
    public const double BusyUsedGiB = 6;

    public static bool ShouldRetry(string? stderr, int attemptNumber)
        => attemptNumber < MaxAttempts && !LooksPermanent(stderr);

    public static bool LooksTransientStartupFailure(string? details)
    {
        if (LooksPermanent(details))
        {
            return false;
        }

        var text = details ?? string.Empty;
        return text.Contains("exited during startup", StringComparison.OrdinalIgnoreCase)
            || text.Contains("CUDA", StringComparison.OrdinalIgnoreCase)
            || text.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
            || text.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksPermanent(string? stderr)
    {
        var text = stderr ?? string.Empty;
        if (text.Length == 0)
        {
            return false;
        }

        return ContainsAny(
            text,
            "unknown argument",
            "unknown option",
            "unrecognized",
            "invalid argument",
            "failed to open",
            "no such file",
            "cannot find the file",
            "gguf_init_from_file: failed",
            "is not a valid gguf");
    }

    public static bool VramLooksBusy(double usedGiB, double totalGiB)
    {
        if (usedGiB < BusyUsedGiB)
        {
            return false;
        }

        if (totalGiB <= 0)
        {
            return true;
        }

        return usedGiB >= Math.Max(BusyUsedGiB, totalGiB * 0.25);
    }

    public static bool VramLooksSettled(
        bool haveBefore,
        double usedBeforeGiB,
        double freeBeforeGiB,
        bool haveNow,
        double usedNowGiB,
        double freeNowGiB)
    {
        if (!haveNow)
        {
            return true;
        }

        if (!haveBefore)
        {
            return !VramLooksBusy(usedNowGiB, usedNowGiB + freeNowGiB);
        }

        if (Math.Abs(freeNowGiB - freeBeforeGiB) < 0.25
            && Math.Abs(usedNowGiB - usedBeforeGiB) < 0.25
            && !VramLooksBusy(usedNowGiB, usedNowGiB + freeNowGiB))
        {
            return true;
        }

        return freeNowGiB >= freeBeforeGiB + VramFreeGainGiB
            || usedNowGiB <= usedBeforeGiB - VramFreeGainGiB;
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
