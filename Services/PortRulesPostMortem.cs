using System;
using System.Globalization;

namespace FluxMux.Avalonia.Services;

/// <summary>
/// After a failed local turn, name the Port rule that tripped using the
/// same label as Servers → Advanced: Port rules, and one change that might
/// help. Does not write Port rules.
/// </summary>
public static class PortRulesPostMortem
{
    public const string WatermarkPercent = "Watermark %";
    public const string Headroom = "Headroom";
    public const string MillAtOmitted = "Mill at omitted";
    public const string ObserveOnlyMill = "Observe-only mill";
    public const string ClosedLoopMillCeiling = "Closed-loop mill ceiling";
    public const string ClosedLoopLookback = "Closed-loop lookback";
    public const string RapidChurnSeconds = "Rapid-churn seconds";
    public const string RapidChurnConsecutive = "Rapid-churn consecutive";
    public const string FirstByteSeconds = "First-byte seconds";
    public const string ThinkTokensPerSecond = "Think tokens / sec";
    public const string MaxThinkFirstByte = "Max think first-byte";
    public const string StallSeconds = "Stall seconds";
    public const string WaitLongerSeconds = "Wait-longer seconds";
    public const string LoadingRetries = "503 retries";
    public const string LoadingRetryDelay = "503 delay seconds";
    public const string StopStrings = "Stop strings";

    public const string NarrowerChatAdvice =
        "Start a new Client-app chat with a narrower ask.";

    public const string NarrowerAskAdvice = NarrowerChatAdvice;

    public const string SmallerNextStepAdvice =
        "Start a new Client-app chat with a smaller next step.";

    public const string ClientAppFaultLead =
        "Changing Port rules will not fix this. The problem is in the Client-app chat.";

    public const string PortRulesLocation = "Servers → Advanced: **Port rules**";

    public const string NextTurnAfterRaise =
        "That change only applies to the next Client-app turn.";

    public const string ChatTurnCannotContinue = "This chat turn cannot continue: ";

    public const string PortRuleStopAdvice =
        "This Client-app turn is over. Another message in this chat will hit the same stop or stall on 'reconnecting'.";

    public const string TemperatureLabel = "Temperature";
    public const string ContextLabel = "Context";
    public const string ImagesLabel = "Images";
    public const string ReasoningLabel = "Reasoning";
    public const string MaxTokensLabel = "Max tokens";

    public const string NextTurnAfterLaunch =
        "That change needs Launch, and only applies to the next Client-app turn.";

    public const string MillBreakAdvice =
        "To break a mill, raise "
        + "**"
        + TemperatureLabel
        + "**"
        + " on this model profile, or start a new Client-app chat with a narrower ask.";

    public const string RaiseContextAdvice =
        "On this model profile, raise **Context** and Launch if this local model can hold more. "
        + NextTurnAfterLaunch;

    public const string EnableImagesAdvice =
        "On this model profile, turn **Images** on and Launch. "
        + NextTurnAfterLaunch;

    public const string EnableReasoningAdvice =
        "On this model profile, set **Reasoning** to On and Launch if you want a visible reply after thought. "
        + NextTurnAfterLaunch;

    public const string RaiseMaxTokensAdvice =
        "On this model profile, raise **Max tokens** so the think budget is larger.";

    public static string MarkRule(string setting)
        => "**" + setting + "**";

    public static string NamePortRule(string setting)
        => "Port rule " + MarkRule(setting);

    public static string StoppedWhenRuleHit(string setting, string hitDetail, string currentSetting)
        => "AI-FluxMux stopped the turn when "
           + NamePortRule(setting)
           + " hit "
           + hitDetail
           + " (the current setting is "
           + currentSetting
           + ").";

    public const string NoPortRuleForOverflow =
        "Changing Port rules will not fix one over-full Client-app message.";

    public static string? FormatMill(int omitted, bool rapidChurn, int rapidStreak, PortForwardingRules? rules)
    {
        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        if (omitted < live.RunawayOmittedResults && !rapidChurn)
        {
            var count = live.ObserveOnlyMillCount.ToString(CultureInfo.InvariantCulture);
            return ChatTurnCannotContinue
                + "the Client app kept taking screenshots or repeating the same commands without reading or editing a project file. "
                + StoppedWhenRuleHit(ObserveOnlyMill, count, count)
                + " "
                + RaiseSettingAdvice(ObserveOnlyMill)
                + " "
                + MillBreakAdvice;
        }

        if (rapidChurn)
        {
            var hit = rapidStreak.ToString(CultureInfo.InvariantCulture)
                + " turns in a row, each less than "
                + live.RapidChurnSeconds.ToString("0.#", CultureInfo.InvariantCulture)
                + "s";
            return ChatTurnCannotContinue
                + "the Client app sent tools too quickly. "
                + StoppedWhenRuleHit(
                    RapidChurnConsecutive,
                    hit,
                    live.RapidChurnConsecutive.ToString(CultureInfo.InvariantCulture))
                + " "
                + RaiseSettingAdvice(RapidChurnConsecutive)
                + " You can also raise "
                + NamePortRule(RapidChurnSeconds)
                + ". "
                + NarrowerChatAdvice;
        }

        if (omitted >= live.ClosedLoopRunawayOmittedResults)
        {
            return ChatTurnCannotContinue
                + "the Client app has run too many tools. "
                + StoppedWhenRuleHit(
                    ClosedLoopMillCeiling,
                    omitted.ToString(CultureInfo.InvariantCulture) + " older tool results",
                    live.ClosedLoopRunawayOmittedResults.ToString(CultureInfo.InvariantCulture))
                + " "
                + RaiseSettingAdvice(ClosedLoopMillCeiling)
                + " "
                + MillBreakAdvice;
        }

        var lookback = live.ClosedLoopMillLookback.ToString(CultureInfo.InvariantCulture);
        var floor = live.RunawayOmittedResults.ToString(CultureInfo.InvariantCulture);
        var ceiling = live.ClosedLoopRunawayOmittedResults.ToString(CultureInfo.InvariantCulture);
        return ChatTurnCannotContinue
            + "the Client app has run too many tools. You already passed "
            + NamePortRule(MillAtOmitted)
            + " ("
            + floor
            + ") while recent tools looked like investigation. "
            + "AI-FluxMux stopped the turn when the last "
            + NamePortRule(ClosedLoopLookback)
            + " ("
            + lookback
            + ") tools no longer looked like a read/edit/search loop ("
            + omitted.ToString(CultureInfo.InvariantCulture)
            + " older tool results; "
            + NamePortRule(ClosedLoopMillCeiling)
            + " is "
            + ceiling
            + "). You can raise "
            + NamePortRule(ClosedLoopLookback)
            + " or "
            + NamePortRule(ClosedLoopMillCeiling)
            + " in "
            + PortRulesLocation
            + ". "
            + NextTurnAfterRaise
            + " "
            + NarrowerChatAdvice;
    }

    public static string RaiseSettingAdvice(string setting)
        => "You can raise "
           + NamePortRule(setting)
           + " in "
           + PortRulesLocation
           + ". "
           + NextTurnAfterRaise;

    public const string SteerNoticeLead =
        "AI-FluxMux paused this turn so you can steer in the Client app. ";

    public const string SteerNoticeCloser =
        "Send another message in this chat with a narrower ask, or raise the Port rule in "
        + PortRulesLocation
        + ".";

    public static string WithoutStopLeads(string? finding)
    {
        var body = (finding ?? string.Empty).Trim();
        if (body.StartsWith(ChatTurnCannotContinue, StringComparison.Ordinal))
        {
            body = body[ChatTurnCannotContinue.Length..].Trim();
        }

        if (body.EndsWith(PortRuleStopAdvice, StringComparison.Ordinal))
        {
            body = body[..^PortRuleStopAdvice.Length].Trim();
        }

        return body;
    }

    public static string FormatPausePrompt(string? finding)
        => WithoutStopLeads(finding);

    public static string FormatSteerNotice(string? finding)
        => JoinClientError(SteerNoticeLead + WithoutStopLeads(finding), SteerNoticeCloser);

    public static string WithStopAdvice(string? body)
        => JoinClientError(body, PortRuleStopAdvice);

    public static string FinishClientError(string? lead, string? details = null)
    {
        var extra = (details ?? string.Empty).Trim();
        if (extra.StartsWith(ChatTurnCannotContinue, StringComparison.Ordinal))
        {
            return WithStopAdvice(extra);
        }

        return WithStopAdvice(JoinClientError(lead, extra));
    }

    public static string JoinClientError(string? lead, string? details)
    {
        var body = (lead ?? string.Empty).Trim();
        var extra = (details ?? string.Empty).Trim();
        if (extra.Length == 0)
        {
            return body;
        }

        if (body.Length == 0)
        {
            return extra;
        }

        return body + " " + extra;
    }

    public static string FormatRepeatedCommand()
        => ChatTurnCannotContinue
            + "the Client app ran the same command twice. "
            + ClientAppFaultLead
            + " "
            + MillBreakAdvice;

    public static string FormatDiagnosticDump(int artifactCount)
        => ChatTurnCannotContinue
            + "the Client app created "
            + Math.Max(0, artifactCount).ToString(CultureInfo.InvariantCulture)
            + " session diagnostic files. "
            + ClientAppFaultLead
            + " Edit the project files instead. "
            + NarrowerChatAdvice;

    public static string? FormatHang(
        string? reason,
        int quietSeconds,
        int waitLongerCount,
        int firstByteDeadlineSeconds,
        PortForwardingRules? rules)
    {
        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        var limitName = HangLimitName(reason, waitLongerCount, firstByteDeadlineSeconds, live);
        var limitValue = HangLimitValue(limitName, firstByteDeadlineSeconds, live);
        return ChatTurnCannotContinue
            + HangWhatHappened(limitName)
            + " "
            + StoppedWhenRuleHit(
                limitName,
                quietSeconds.ToString(CultureInfo.InvariantCulture) + "s quiet",
                limitValue.ToString(CultureInfo.InvariantCulture))
            + " "
            + HangChangeAdvice(limitName)
            + " "
            + NarrowerChatAdvice;
    }

    public static string? FormatLoading503(int retriesUsed, PortForwardingRules? rules)
    {
        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        return ChatTurnCannotContinue
            + "llama-server was still loading and did not take this turn. "
            + StoppedWhenRuleHit(
                LoadingRetries,
                retriesUsed.ToString(CultureInfo.InvariantCulture) + " tries",
                live.LoadingRetryCount.ToString(CultureInfo.InvariantCulture))
            + " You can raise "
            + NamePortRule(LoadingRetries)
            + " or "
            + NamePortRule(LoadingRetryDelay)
            + " in "
            + PortRulesLocation
            + ". "
            + NextTurnAfterRaise;
    }

    public static string? FormatThinkOnly(PortForwardingRules? rules)
    {
        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        var stopAdvice = live.StopHygieneMode == PortForwardingRules.StopIgnore
            ? NamePortRule(StopStrings)
              + " is already Ignore Client-app stop in "
              + PortRulesLocation
              + ", so a stop list did not cut this reply."
            : "If a Client-app stop list cut the reply, set "
              + NamePortRule(StopStrings)
              + " to Ignore Client-app stop in "
              + PortRulesLocation
              + ".";
        var changeBit = live.StopHygieneMode == PortForwardingRules.StopIgnore
            ? string.Empty
            : " " + NextTurnAfterRaise;
        return ChatTurnCannotContinue
            + "llama-server only thought; no reply or tool call reached the Client app. "
            + stopAdvice
            + changeBit
            + " "
            + EnableReasoningAdvice
            + " "
            + NarrowerChatAdvice;
    }

    public static string? FormatContextOverflow(bool compactApplied, PortForwardingRules? rules)
    {
        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        var compactBit = compactApplied
            ? "Compact already ran (" + NamePortRule(WatermarkPercent) + " is " + live.CompactWatermarkPercent.ToString(CultureInfo.InvariantCulture) + ")."
            : "Compact did not shorten this turn.";
        return ChatTurnCannotContinue
            + "this turn was too large for the loaded Context. "
            + compactBit
            + " Compact cannot shorten one over-full Client-app message. "
            + NamePortRule(Headroom)
            + " is "
            + live.CompactHeadroom.ToString(CultureInfo.InvariantCulture)
            + ". Raising "
            + NamePortRule(Headroom)
            + " or "
            + NamePortRule(WatermarkPercent)
            + " will not fix this turn. "
            + NoPortRuleForOverflow
            + " Those settings are in "
            + PortRulesLocation
            + ". "
            + RaiseContextAdvice
            + " "
            + SmallerNextStepAdvice;
    }

    public static string HangLimitName(
        string? reason,
        int waitLongerCount,
        int firstByteDeadlineSeconds,
        PortForwardingRules rules)
    {
        if (waitLongerCount > 0)
        {
            return WaitLongerSeconds;
        }

        if (string.Equals(reason, LocalStreamHangPolicy.StallReason, StringComparison.OrdinalIgnoreCase))
        {
            return StallSeconds;
        }

        var live = (rules ?? PortForwardingRules.Defaults).Clamp();
        if (firstByteDeadlineSeconds >= live.MaxThinkFirstByteSeconds)
        {
            return MaxThinkFirstByte;
        }

        if (firstByteDeadlineSeconds <= live.FirstByteSeconds)
        {
            return FirstByteSeconds;
        }

        return ThinkTokensPerSecond;
    }

    private static int HangLimitValue(string limitName, int firstByteDeadlineSeconds, PortForwardingRules live)
    {
        if (limitName == WaitLongerSeconds)
        {
            return live.WaitLongerSeconds;
        }

        if (limitName == StallSeconds)
        {
            return live.StallSeconds;
        }

        if (limitName == MaxThinkFirstByte)
        {
            return live.MaxThinkFirstByteSeconds;
        }

        if (limitName == ThinkTokensPerSecond)
        {
            return firstByteDeadlineSeconds;
        }

        return live.FirstByteSeconds;
    }

    private static string HangWhatHappened(string limitName)
    {
        if (limitName == WaitLongerSeconds)
        {
            return "you chose to keep this turn open, and llama-server stayed quiet.";
        }

        if (limitName == StallSeconds)
        {
            return "llama-server went quiet in the middle of a reply.";
        }

        return "llama-server did not start a reply in time.";
    }

    private static string HangChangeAdvice(string limitName)
    {
        if (limitName == WaitLongerSeconds)
        {
            return RaiseSettingAdvice(WaitLongerSeconds);
        }

        if (limitName == StallSeconds)
        {
            return RaiseSettingAdvice(StallSeconds);
        }

        if (limitName == MaxThinkFirstByte)
        {
            return "You can raise "
                + NamePortRule(MaxThinkFirstByte)
                + " in "
                + PortRulesLocation
                + " if this model thinks before it writes. "
                + NextTurnAfterRaise;
        }

        if (limitName == ThinkTokensPerSecond)
        {
            return "You can raise "
                + NamePortRule(MaxThinkFirstByte)
                + " or lower "
                + NamePortRule(ThinkTokensPerSecond)
                + " in "
                + PortRulesLocation
                + " so a thinking model has more time. "
                + NextTurnAfterRaise;
        }

        return RaiseSettingAdvice(FirstByteSeconds)
            + " If this model thinks first, raise "
            + NamePortRule(MaxThinkFirstByte)
            + " instead.";
    }
}
