using System;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// After Switch to cloud, later turns that the loaded local could take
/// should be offered back. A hot hop (ready cloud or already-loaded local)
/// keeps this Client-app chat, including Harness.
/// </summary>
public static class CloudReturnToLocalPolicy
{
    public const string Source = "return_local";
    public const int OpenSwapSufficientTurns = 1;

    public static int RequiredSufficientTurns(bool harnessEndpoint)
    {
        _ = harnessEndpoint;
        return OpenSwapSufficientTurns;
    }

    public static bool LocalWouldHaveSufficed(string? naturalKind)
        => string.Equals(naturalKind, FluxMuxGatewayRouting.Local, StringComparison.OrdinalIgnoreCase);

    public static int NextStreak(int current, bool localWouldHaveSufficed)
        => localWouldHaveSufficed ? Math.Max(0, current) + 1 : 0;

    public static bool RearmAfterThisTurn(bool localWouldHaveSufficed, bool currentlyArmed)
        => !localWouldHaveSufficed || currentlyArmed;

    public static bool ShouldOffer(
        bool inApprovedCloudWindow,
        bool localReady,
        bool localWouldHaveSufficed,
        bool returnArmed,
        int streakAfterThisTurn,
        bool harnessEndpoint,
        bool alreadyPendingReturn)
    {
        if (!inApprovedCloudWindow
            || !localReady
            || !localWouldHaveSufficed
            || !returnArmed
            || alreadyPendingReturn)
        {
            return false;
        }

        return streakAfterThisTurn >= RequiredSufficientTurns(harnessEndpoint);
    }

    public static bool WaitForThisTurn(bool harnessEndpoint)
    {
        _ = harnessEndpoint;
        return false;
    }

    public static bool IsReturnToLocalSource(string? source)
        => string.Equals(source, Source, StringComparison.OrdinalIgnoreCase);

    public static bool StillHonorCloudWindow(
        string? recommendStatus,
        string? recommendSource,
        long allowUntilUnix,
        long nowUnix)
    {
        if (allowUntilUnix <= nowUnix)
        {
            return false;
        }

        if (string.Equals(recommendStatus, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(recommendStatus, "pending", StringComparison.OrdinalIgnoreCase)
               && IsReturnToLocalSource(recommendSource);
    }

    public static string FormatReason(string localLabel, bool harnessEndpoint)
    {
        var name = string.IsNullOrWhiteSpace(localLabel) ? "the loaded local" : localLabel.Trim();
        _ = harnessEndpoint;
        return "This turn continues on the ready cloud. It also fits " + name
            + ". Switch back to that model? This Client-app chat can continue.";
    }
}
