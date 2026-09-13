using System;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// Shared recovery prompt after a turn is aborted (local hang or cloud failure).
/// Button labels stay model-neutral so the same dialog works on either side.
/// </summary>
public static class RouteRecoveryPolicy
{
    public const string ResumeWaitingLabel = "Keep current model — end this turn";
    public const string StayLocalContinueLabel = "Keep current model — this turn continues";
    public const string WaitLongerLabel = "Continue waiting";
    public const string SendAnywayLabel = "Send this turn anyway";
    public const string EndThisTurnLabel = "End this turn";
    public const string SteerLabel = "Let me steer";
    public const string SwitchLabelPrefix = "Switch to";
    public const string CompactLabel = "Compact";
    public const string LoadSuggestedLabel = "Load suggested model profile";
    public const string KeepProfileLabel = "Keep current model profile";
    public const string WaitStatus = "wait";
    public const string SteerStatus = "steer";
    public const string PortRulePauseSource = "port_rule_pause";
    public const string LocalHangSource = "local_hang";
    public const string LocalHangAbortSource = "local_hang_abort";
    public const string CloudFailSource = "cloud_fail";
    public const string CapacitySource = "capacity";
    public const string LocalCannotSource = "local_cannot";
    public const string ToolLoopSource = "tool_loop";
    public const string ReturnToLocalSource = CloudReturnToLocalPolicy.Source;

    public const string SwitchHarnessRelaunchSuffix = " — open Harness chat from this slot";
    public const string HarnessRelaunchFromNewSlot =
        ClineSwitchSurvivalPolicy.HarnessIfAlsoOnPortAdvice;
    public const string HarnessRelaunchFromNewSlotAdvice =
        ClineSwitchSurvivalPolicy.HarnessIfAlsoOnPortAdvice;
    public const string HarnessRelaunchHint =
        ClineSwitchSurvivalPolicy.HarnessIfAlsoOnPortAdvice;

    public static bool ShouldShowHarnessRelaunchHint(
        bool harnessNeedsRelaunch,
        bool continueWaitingOffered,
        string? source = null)
        => harnessNeedsRelaunch
            && !continueWaitingOffered
            && (IsCloudFailureSource(source)
                || IsLocalHangAbortSource(source));

    public static bool IsHotHopSource(string? source)
        => IsCapacitySource(source)
            || IsLocalCannotSource(source)
            || CloudReturnToLocalPolicy.IsReturnToLocalSource(source);

    public static bool IsCapacitySource(string? source)
        => string.Equals(source, CapacitySource, StringComparison.OrdinalIgnoreCase);

    public static bool IsLocalCannotSource(string? source)
        => string.Equals(source, LocalCannotSource, StringComparison.OrdinalIgnoreCase);

    public static bool ShouldShowCapacityPrompt(bool cloudReady)
        => cloudReady;

    public static string FormatResumeLabel(string? source)
    {
        if (IsPortRulePauseSource(source))
        {
            return EndThisTurnLabel;
        }

        return IsHotHopSource(source)
            ? StayLocalContinueLabel
            : ResumeWaitingLabel;
    }

    public static string FormatWaitLongerLabel(string? source)
        => IsPortRulePauseSource(source) ? SendAnywayLabel : WaitLongerLabel;

    public static string FormatSwitchLabel(string? modelDisplayName, bool harnessNeedsRelaunch = false)
    {
        var name = string.IsNullOrWhiteSpace(modelDisplayName)
            ? "the other model"
            : modelDisplayName.Trim();
        var label = SwitchLabelPrefix + " " + name;
        return harnessNeedsRelaunch ? label + SwitchHarnessRelaunchSuffix : label;
    }

    public static string FormatResumeTooltip(string? endpointApp, bool turnContinues, string? source = null)
    {
        _ = endpointApp;
        if (IsPortRulePauseSource(source))
        {
            return "End this Client-app turn. llama-server is not asked.";
        }

        if (turnContinues)
        {
            return "Keep the current model. This turn continues.";
        }

        return "End this turn and keep the current model. " + ClineSwitchSurvivalPolicy.FormatFailedTurnAdvice();
    }

    public static string FormatWaitLongerTooltip(string? source)
        => IsPortRulePauseSource(source)
            ? "Forward this request to llama-server once. Does not raise the saved Port rule."
            : "Keep this Client-app turn open. llama-server keeps working. The same choices appear again if it stays quiet.";

    public static string FormatSteerTooltip()
        => "Finish this turn with a notice so you can send the next Client-app message and steer.";

    public static string FormatSwitchTooltip(string? endpointApp, bool cloud, bool turnContinues)
    {
        _ = endpointApp;
        var dest = cloud
            ? "Route the next turns to this cloud model for about 15 minutes."
            : "Route the next turns to this local model.";
        if (turnContinues)
        {
            return dest + " This Client-app chat can continue.";
        }

        return dest + " " + ClineSwitchSurvivalPolicy.FormatFailedTurnAdvice();
    }

    public static string FormatKeepCurrentStatus(string? endpointApp)
        => "Keeping the current model. " + ClineSwitchSurvivalPolicy.FormatFailedTurnAdvice(endpointApp);

    public static bool IsCloudFailureSource(string? source)
        => string.Equals(source, CloudFailSource, StringComparison.OrdinalIgnoreCase);

    public static bool IsLocalHangSource(string? source)
        => string.Equals(source, LocalHangSource, StringComparison.OrdinalIgnoreCase);

    public static bool IsLocalHangAbortSource(string? source)
        => string.Equals(source, LocalHangAbortSource, StringComparison.OrdinalIgnoreCase);

    public static bool IsToolLoopSource(string? source)
        => string.Equals(source, ToolLoopSource, StringComparison.OrdinalIgnoreCase);

    public static bool IsPortRulePauseSource(string? source)
        => string.Equals(source, PortRulePauseSource, StringComparison.OrdinalIgnoreCase);

    public static bool IsWaitStatus(string? status)
        => string.Equals(status, WaitStatus, StringComparison.OrdinalIgnoreCase);

    public static bool IsSteerStatus(string? status)
        => string.Equals(status, SteerStatus, StringComparison.OrdinalIgnoreCase);

    public static bool IsTimeoutStatus(string? status)
        => string.Equals(status, "timeout", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// RAM-spill / explicit-cloud consent that times out and stays local must not leave
    /// the decision popup up. Filling timeouts still need the banner (turn already 400'd).
    /// </summary>
    public static bool ShouldKeepTimedOutCloudRecommend(bool failOnTimeout)
        => failOnTimeout;

    /// <summary>
    /// One recovery family at a time: a concrete Load suggested beats a concurrent
    /// filling-turn cloud consent. RAM-spill and explicit "use cloud" stay cloud.
    /// </summary>
    /// <summary>
    /// Hold the Client-app HTTP request for Switch to a ready cloud,
    /// including filling turns. A Load suggested reload is not safe in this
    /// chat and must not 400 the turn before that hop can finish.
    /// </summary>
    public static bool HoldInFlightRequestForCloudConsent()
        => true;

    public static bool PreferLocalReloadOverCloudConsent(
        bool localReloadHasCandidate,
        string? consentReason)
    {
        _ = localReloadHasCandidate;
        _ = consentReason;
        return false;
    }
}
