using System;
using System.Threading;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Hangproof local SSE copies: abort if llama-server never starts streaming,
/// or if it goes silent mid-stream. The gateway then ends the Client-app
/// stream with a finished assistant turn and data: [DONE] so Deep Dive can stop.
/// </summary>
public static class LocalStreamHangPolicy
{
    public const int FirstByteSeconds = 25;
    public const int ThinkTokensPerSecond = 20;
    public const int MaxThinkFirstByteSeconds = 180;
    public const int StallSeconds = 45;
    public const int WaitLongerSeconds = 90;
    public const int DecisionSeconds = 45;
    public const int MinCopyTimeoutSeconds = FirstByteSeconds + DecisionSeconds + WaitLongerSeconds + DecisionSeconds;
    public const int MaxCopyTimeoutSeconds = 900;
    public const int CopyTimeoutExtendSeconds = WaitLongerSeconds + DecisionSeconds + 30;
    public const string FirstByteReason = "first_byte";
    public const string StallReason = "stall";
    public const string RecoveredStatus = "recovered";

    public static string HangWaitMessage => FormatHangWaitMessage(null);

    public const string HangAbortMessage =
        PortRulesPostMortem.ChatTurnCannotContinue
        + "llama-server did not start a reply in time. "
        + PortRulesPostMortem.NarrowerChatAdvice
        + " "
        + PortRulesPostMortem.PortRuleStopAdvice;
    public const string HangAbortHint =
        "AI-FluxMux ended this stream so the Client app can stop waiting. llama-server is parked so the AI-FluxMux window stays usable. "
        + PortRulesPostMortem.NarrowerChatAdvice
        + " "
        + PortRulesPostMortem.PortRuleStopAdvice;

    public static string FormatHangWaitMessage(string? endpointApp)
    {
        _ = endpointApp;
        var wait = ControlLabelMarkup.Mark(RouteRecoveryPolicy.WaitLongerLabel);
        var keep = ControlLabelMarkup.Mark(RouteRecoveryPolicy.ResumeWaitingLabel);
        var switchTo = ControlLabelMarkup.Mark(RouteRecoveryPolicy.SwitchLabelPrefix);
        return "llama-server is still quiet. Models that think first (for example Qwen) can sit without sending tokens for a while. Note that AI-FluxMux is watching llama-server, not the Client app. The Client app may keep working (tools, screenshots) while this prompt is open, and this prompt closes if llama-server starts sending. Choose "
            + wait + " to keep this turn open. The same choices will appear again if llama-server stays quiet. If you choose "
            + keep + " or " + switchTo + " the other ready model, "
            + PortRulesPostMortem.NarrowerChatAdvice
            + " "
            + PortRulesPostMortem.PortRuleStopAdvice;
    }

    public static string FormatHangAbortMessage(string? endpointApp)
        => FormatHangAbortMessage(endpointApp, portRuleDetails: null);

    public static string FormatHangAbortMessage(string? endpointApp, string? portRuleDetails)
    {
        _ = endpointApp;
        if (string.IsNullOrWhiteSpace(portRuleDetails))
        {
            return HangAbortMessage;
        }

        return PortRulesPostMortem.WithStopAdvice(portRuleDetails);
    }

    public static string FormatHangAbortHint(string? endpointApp)
    {
        _ = endpointApp;
        return HangAbortHint;
    }

    public static int ClampCopyTimeoutSeconds(int estimatedSeconds)
        => ClampCopyTimeoutSeconds(estimatedSeconds, thinkBudget: 0, PortForwardingRules.Defaults);

    public static int ClampCopyTimeoutSeconds(int estimatedSeconds, int? thinkBudget)
        => ClampCopyTimeoutSeconds(estimatedSeconds, thinkBudget, PortForwardingRules.Defaults);

    public static int ClampCopyTimeoutSeconds(int estimatedSeconds, int? thinkBudget, PortForwardingRules rules)
    {
        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        var firstByte = (int)FirstByteDeadlineForThinkBudget(thinkBudget, live).TotalSeconds;
        var min = Math.Max(
            live.MinCopyTimeoutSeconds,
            firstByte + live.DecisionSeconds + live.WaitLongerSeconds + live.DecisionSeconds);
        return Math.Max(min, Math.Min(MaxCopyTimeoutSeconds, estimatedSeconds));
    }

    /// <summary>
    /// Low/Medium sit in think without SSE bytes. Do not open the hang
    /// dialog until that budget could have been spent.
    /// </summary>
    public static TimeSpan FirstByteDeadlineForThinkBudget(int? thinkBudget)
        => FirstByteDeadlineForThinkBudget(thinkBudget, PortForwardingRules.Defaults);

    public static TimeSpan FirstByteDeadlineForThinkBudget(int? thinkBudget, PortForwardingRules rules)
    {
        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        if (thinkBudget is null)
        {
            return TimeSpan.FromSeconds(live.WaitLongerSeconds);
        }

        if (thinkBudget.Value <= 0)
        {
            return TimeSpan.FromSeconds(live.FirstByteSeconds);
        }

        var seconds = (int)Math.Ceiling(thinkBudget.Value / (double)live.ThinkTokensPerSecond);
        return TimeSpan.FromSeconds(Math.Clamp(seconds, live.FirstByteSeconds, live.MaxThinkFirstByteSeconds));
    }

    public static void ExtendCopyTimeout(CancellationTokenSource timeoutCts)
        => ExtendCopyTimeout(timeoutCts, PortForwardingRules.Defaults);

    public static void ExtendCopyTimeout(CancellationTokenSource timeoutCts, PortForwardingRules rules)
        => timeoutCts.CancelAfter(TimeSpan.FromSeconds((rules ?? PortForwardingRules.Defaults).Clamp().CopyTimeoutExtendSeconds));

    public static bool ShouldAbort(
        bool gotUpstreamBytes,
        TimeSpan sinceCopyStart,
        TimeSpan sinceLastUpstreamByte,
        out string reason)
        => ShouldAbort(
            gotUpstreamBytes,
            sinceCopyStart,
            sinceLastUpstreamByte,
            TimeSpan.FromSeconds(FirstByteSeconds),
            TimeSpan.FromSeconds(StallSeconds),
            out reason);

    public static bool ShouldAbort(
        bool gotUpstreamBytes,
        TimeSpan sinceCopyStart,
        TimeSpan sinceLastUpstreamByte,
        TimeSpan firstByteDeadline,
        TimeSpan stallDeadline,
        out string reason)
    {
        if (!gotUpstreamBytes && sinceCopyStart >= firstByteDeadline)
        {
            reason = FirstByteReason;
            return true;
        }

        if (gotUpstreamBytes && sinceLastUpstreamByte >= stallDeadline)
        {
            reason = StallReason;
            return true;
        }

        reason = string.Empty;
        return false;
    }

    public static bool StreamRecovered(
        bool gotUpstreamBytes,
        TimeSpan sinceLastUpstreamByte,
        TimeSpan stallDeadline)
        => gotUpstreamBytes && sinceLastUpstreamByte < stallDeadline;
}

public sealed class LocalStreamHangClock
{
    public LocalStreamHangClock()
        : this(PortForwardingRules.Defaults)
    {
    }

    public LocalStreamHangClock(PortForwardingRules rules)
    {
        Rules = (rules ?? PortForwardingRules.Defaults).Clamp();
        FirstByteDeadline = TimeSpan.FromSeconds(Rules.FirstByteSeconds);
        StallDeadline = TimeSpan.FromSeconds(Rules.StallSeconds);
    }

    public PortForwardingRules Rules { get; }

    public DateTime CopyStartUtc { get; set; } = DateTime.UtcNow;

    public DateTime LastByteUtc { get; set; } = DateTime.UtcNow;

    public TimeSpan FirstByteDeadline { get; set; }

    public TimeSpan StallDeadline { get; set; }

    public string LastAbortReason { get; set; } = string.Empty;

    public int WaitLongerCount { get; set; }

    public void NoteByte() => LastByteUtc = DateTime.UtcNow;

    public void ResetForWaitLonger()
    {
        WaitLongerCount++;
        CopyStartUtc = DateTime.UtcNow;
        LastByteUtc = DateTime.UtcNow;
        FirstByteDeadline = TimeSpan.FromSeconds(Rules.WaitLongerSeconds);
        StallDeadline = TimeSpan.FromSeconds(Rules.WaitLongerSeconds);
    }
}
