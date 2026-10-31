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

    /// <summary>
    /// Retry budget for profiles that load slowly from cold — YaRN / extended-context
    /// profiles that allocate a large KV cache and use GPU+CPU offload. A cold load of
    /// these can take minutes (disk I/O + big KV allocation), far longer than the
    /// default 6×2s=12s window. Without a longer window the gateway gives up, converts
    /// the "still loading" 503 into a non-transient error, and the launch loop fails
    /// even though llama-server would have been ready a moment later.
    /// </summary>
    public const int LongLoadRetryCount = 150;
    public const int LongLoadDelaySeconds = 2;
    public const int LongLoadMaxTotalWaitSeconds = 300;

    /// <summary>
    /// True when the active local profile is one that loads slowly from cold: an
    /// extended-context (YaRN) profile, or a profile that spills KV into system RAM
    /// (GPU+CPU / CPU-only offload). These are the profiles whose cold load can exceed
    /// the default retry window.
    /// </summary>
    public static bool IsLongLoadProfile(int localContext, string gpuOffloadMode)
    {
        var offload = (gpuOffloadMode ?? string.Empty).Trim().ToLowerInvariant();
        bool spillsToRam = offload.Contains("cpu", StringComparison.OrdinalIgnoreCase);
        // Extended context: well above the native 128K window is a strong YaRN signal.
        bool extendedContext = localContext > 131072;
        return extendedContext || spillsToRam;
    }

    /// <summary>
    /// The effective retry count for the active profile: the long-load budget when the
    /// profile loads slowly from cold, otherwise the user-configured count.
    ///
    /// IMPORTANT: A cold load of ANY profile (not just long-load/YaRN) can take 15–60s
    /// (loading a large GGUF into GPU+RAM). The gateway's 503-retry loop must use the
    /// long-load budget for ANY local 503 "Loading model" error, otherwise it bails after
    /// ~12s (6 retries × 2s) — right when a ~15–20s cold load is about to finish — and the
    /// client sees a failure while the model is still loading in the background.
    /// The <paramref name="isDaemonLoading"/> flag indicates the 503 is a genuine
    /// "still loading" error (not a different kind of failure), so the long budget
    /// applies regardless of the profile's context/offload settings.
    /// </summary>
    public static int EffectiveRetryCount(PortForwardingRules? rules, int localContext, string gpuOffloadMode, bool isDaemonLoading = false)
    {
        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        // Use the long-load budget when the profile is a long-load profile OR when the
        // 503 is a genuine "still loading" error (a cold load of any profile can take
        // minutes, and the gateway must not bail early and misreport it as a failure).
        if (IsLongLoadProfile(localContext, gpuOffloadMode) || isDaemonLoading)
        {
            return Math.Max(live.LoadingRetryCount, LongLoadRetryCount);
        }
        return live.LoadingRetryCount;
    }

    /// <summary>
    /// The effective total-wait cap for the active profile.
    ///
    /// Like <see cref="EffectiveRetryCount"/>, a cold load of ANY profile can take
    /// minutes, so the long-load wait cap applies when the 503 is a genuine
    /// "still loading" error, not just for long-load profiles.
    /// </summary>
    public static int EffectiveMaxTotalWaitSeconds(int localContext, string gpuOffloadMode, bool isDaemonLoading = false)
        => (IsLongLoadProfile(localContext, gpuOffloadMode) || isDaemonLoading) ? LongLoadMaxTotalWaitSeconds : MaxTotalWaitSeconds;

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
        PortForwardingRules? rules = null,
        int localContext = 0,
        string gpuOffloadMode = "")
    {
        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        bool isDaemonLoading = LooksLikeDaemonLoading(status, detail);
        // Pass isDaemonLoading so the long-load retry budget applies to ANY local 503
        // "Loading model" error, not just when the profile is detected as long-load from
        // state. A cold load of any profile takes 15–60s; the gateway must not bail after
        // ~12s (right when the load is about to finish) and misreport it as a failure.
        var retryCount = EffectiveRetryCount(rules, localContext, gpuOffloadMode, isDaemonLoading);
        var maxWait = EffectiveMaxTotalWaitSeconds(localContext, gpuOffloadMode, isDaemonLoading);
        // For ANY local 503 "Loading model" error, retry. The Loading503Enabled gate is too
        // restrictive — it should be bypassed for all local 503 "Loading model" errors, not
        // just long-load profiles. Without this, a cold load of a standard profile would fail
        // immediately on the first 503 "Loading model" error if Port rules are off (the default).
        bool isLongLoad = IsLongLoadProfile(localContext, gpuOffloadMode);
        if ((!live.Loading503Enabled && !isLongLoad && !isDaemonLoading) || retriesSoFar >= retryCount)
        {
            return false;
        }

        if (retriesSoFar * live.LoadingRetryDelaySeconds >= maxWait)
        {
            return false;
        }

        return isDaemonLoading;
    }
}
