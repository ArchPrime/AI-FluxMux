using System;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Retry llama-server Loading model / 503 on Port. Does not retry FluxMux's
/// own route_unavailable (no model launched).
/// </summary>
public static class LocalLoadingRetryPolicy
{
    public const int DefaultRetryCount = 6;
    public const int DefaultDelaySeconds = 2;
    public const int MaxTotalWaitSeconds = 30;

    public static bool LooksLikeDaemonLoading(int status, string? detail)
    {
        if (status != 503)
        {
            return false;
        }

        var text = detail ?? string.Empty;
        return text.Contains("Loading model", StringComparison.OrdinalIgnoreCase)
            || text.Contains("model is loading", StringComparison.OrdinalIgnoreCase)
            || text.Contains("still loading", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not ready for completions", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldRetry(
        int status,
        string? detail,
        int retriesSoFar,
        PortForwardingRules? rules = null)
    {
        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        if (!live.Loading503Enabled || retriesSoFar >= live.LoadingRetryCount)
        {
            return false;
        }

        if (retriesSoFar * live.LoadingRetryDelaySeconds >= MaxTotalWaitSeconds)
        {
            return false;
        }

        return LooksLikeDaemonLoading(status, detail);
    }
}
