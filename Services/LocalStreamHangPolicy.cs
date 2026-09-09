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
    public const int StallSeconds = 45;
    public const int WaitLongerSeconds = 90;
    public const int DecisionSeconds = 45;
    public const int MinCopyTimeoutSeconds = FirstByteSeconds + DecisionSeconds + WaitLongerSeconds + DecisionSeconds;
    public const int MaxCopyTimeoutSeconds = 900;
    public const int CopyTimeoutExtendSeconds = WaitLongerSeconds + DecisionSeconds + 30;
    public const string FirstByteReason = "first_byte";
    public const string StallReason = "stall";

    public static string HangWaitMessage => FormatHangWaitMessage(null);

    public const string HangAbortMessage =
        "This turn was aborted: the local model did not start a reply in time. This Cline turn is over. Start a new Cline task if the same error would repeat. If Harness is also using Port, do not continue that Harness chat — start a fresh Harness chat from Quick Select.";
    public const string HangAbortHint =
        "AI-FluxMux ended this stream so the Client app can stop waiting. Cline may still look busy; another message in this task still sends the old thread. If Harness is also using Port, Deep Dive can look busy too. llama-server is parked so the AI-FluxMux window stays usable. Start a new Cline task. If Harness is also using Port, start a fresh Harness chat from Quick Select. You may need to use a stronger model, or ask a simpler question.";

    public static string FormatHangWaitMessage(string? endpointApp)
    {
        _ = endpointApp;
        var wait = ControlLabelMarkup.Mark(RouteRecoveryPolicy.WaitLongerLabel);
        var keep = ControlLabelMarkup.Mark(RouteRecoveryPolicy.ResumeWaitingLabel);
        var switchTo = ControlLabelMarkup.Mark(RouteRecoveryPolicy.SwitchLabelPrefix);
        return "llama-server is still quiet. Models that think first (for example Qwen) can sit without sending tokens for a while. Note that AI-FluxMux is watching llama-server, not the Client app. Cline may end a stalled turn on its own. Harness may end a stalled turn, or may look like it is still working normally. Choose "
            + wait + " to keep this turn open. The same choices will appear again if llama-server stays quiet. If you choose "
            + keep + " or " + switchTo + " the other ready model, start a new Cline task if the same stall would repeat. If Harness is also using Port, start a fresh Harness chat from Quick Select. You may need to use a stronger model, or ask a simpler question.";
    }

    public static string FormatHangAbortMessage(string? endpointApp)
    {
        _ = endpointApp;
        return HangAbortMessage;
    }

    public static string FormatHangAbortHint(string? endpointApp)
    {
        _ = endpointApp;
        return HangAbortHint;
    }

    public static int ClampCopyTimeoutSeconds(int estimatedSeconds)
        => Math.Max(MinCopyTimeoutSeconds, Math.Min(MaxCopyTimeoutSeconds, estimatedSeconds));

    public static void ExtendCopyTimeout(CancellationTokenSource timeoutCts)
        => timeoutCts.CancelAfter(TimeSpan.FromSeconds(CopyTimeoutExtendSeconds));

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
}

public sealed class LocalStreamHangClock
{
    public DateTime CopyStartUtc { get; set; } = DateTime.UtcNow;

    public DateTime LastByteUtc { get; set; } = DateTime.UtcNow;

    public TimeSpan FirstByteDeadline { get; set; } = TimeSpan.FromSeconds(LocalStreamHangPolicy.FirstByteSeconds);

    public TimeSpan StallDeadline { get; set; } = TimeSpan.FromSeconds(LocalStreamHangPolicy.StallSeconds);

    public void NoteByte() => LastByteUtc = DateTime.UtcNow;

    public void ResetForWaitLonger()
    {
        CopyStartUtc = DateTime.UtcNow;
        LastByteUtc = DateTime.UtcNow;
        FirstByteDeadline = TimeSpan.FromSeconds(LocalStreamHangPolicy.WaitLongerSeconds);
        StallDeadline = TimeSpan.FromSeconds(LocalStreamHangPolicy.WaitLongerSeconds);
    }
}
