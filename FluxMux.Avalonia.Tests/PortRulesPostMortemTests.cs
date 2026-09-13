using System;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class PortRulesPostMortemTests
{
    [Fact]
    public void First_Port_rule_name_is_introduced_as_a_Port_rule()
    {
        AssertFirstPortRuleIsNamed(
            PortRulesPostMortem.FormatMill(32, rapidChurn: false, rapidStreak: 0, PortForwardingRules.Defaults),
            PortRulesPostMortem.MillAtOmitted);
        AssertFirstPortRuleIsNamed(
            PortRulesPostMortem.FormatMill(0, rapidChurn: false, rapidStreak: 0, PortForwardingRules.Defaults),
            PortRulesPostMortem.ObserveOnlyMill);
        AssertFirstPortRuleIsNamed(
            PortRulesPostMortem.FormatMill(40, rapidChurn: true, rapidStreak: 4, PortForwardingRules.Defaults),
            PortRulesPostMortem.RapidChurnConsecutive);
        AssertFirstPortRuleIsNamed(
            PortRulesPostMortem.FormatHang(
                LocalStreamHangPolicy.FirstByteReason,
                quietSeconds: 25,
                waitLongerCount: 0,
                firstByteDeadlineSeconds: 25,
                PortForwardingRules.Defaults),
            PortRulesPostMortem.FirstByteSeconds);
        AssertFirstPortRuleIsNamed(
            PortRulesPostMortem.FormatLoading503(6, PortForwardingRules.Defaults),
            PortRulesPostMortem.LoadingRetries);
        AssertFirstPortRuleIsNamed(
            PortRulesPostMortem.FormatThinkOnly(PortForwardingRules.Defaults),
            PortRulesPostMortem.StopStrings);
        AssertFirstPortRuleIsNamed(
            PortRulesPostMortem.FormatContextOverflow(compactApplied: true, PortForwardingRules.Defaults),
            PortRulesPostMortem.WatermarkPercent);
        AssertFirstPortRuleIsNamed(
            PortRulesPostMortem.FormatContextOverflow(compactApplied: false, PortForwardingRules.Defaults),
            PortRulesPostMortem.Headroom);
    }

    private static void AssertFirstPortRuleIsNamed(string? text, string setting)
    {
        Assert.False(string.IsNullOrWhiteSpace(text));
        var marked = PortRulesPostMortem.MarkRule(setting);
        var at = text!.IndexOf(marked, StringComparison.Ordinal);
        Assert.True(at >= 0, "missing " + setting);
        Assert.Equal(
            PortRulesPostMortem.NamePortRule(setting),
            text.Substring(at - "Port rule ".Length, "Port rule ".Length + marked.Length));
    }

    [Fact]
    public void Observe_only_mill_names_Observe_only_mill()
    {
        var text = PortRulesPostMortem.FormatMill(0, rapidChurn: false, rapidStreak: 0, PortForwardingRules.Defaults);
        Assert.Contains("**Observe-only mill**", text);
        Assert.Contains("the current setting is 8", text);
        Assert.Contains("screenshots or repeating the same commands", text);
        Assert.DoesNotContain("game", text, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains(PortRulesPostMortem.RaiseSettingAdvice(PortRulesPostMortem.ObserveOnlyMill), text);
        Assert.Contains(PortRulesPostMortem.MillBreakAdvice, text);
        Assert.Contains(PortRulesPostMortem.PortRulesLocation, text);
        Assert.DoesNotContain("Mill at omitted", text);
    }

    [Fact]
    public void Mill_names_Mill_at_omitted_and_says_leave_it()
    {
        var text = PortRulesPostMortem.FormatMill(32, rapidChurn: false, rapidStreak: 0, PortForwardingRules.Defaults);
        Assert.Contains("the Client app has run too many tools", text);
        Assert.Contains("**Mill at omitted**", text);
        Assert.Contains("You already passed", text);
        Assert.Contains("**Closed-loop lookback**", text);
        Assert.Contains("**Closed-loop mill ceiling**", text);
        Assert.DoesNotContain(PortRulesPostMortem.RaiseSettingAdvice(PortRulesPostMortem.MillAtOmitted), text);
        Assert.Contains(PortRulesPostMortem.PortRulesLocation, text);
        Assert.Contains(PortRulesPostMortem.NarrowerChatAdvice, text);
        Assert.DoesNotContain(PortRulesPostMortem.SmallerNextStepAdvice, text);
    }

    [Fact]
    public void Long_closed_loop_names_Closed_loop_mill_ceiling()
    {
        var text = PortRulesPostMortem.FormatMill(256, rapidChurn: false, rapidStreak: 0, PortForwardingRules.Defaults);
        Assert.Contains("Closed-loop mill ceiling", text);
        Assert.Contains("the current setting is 256", text);
        Assert.Contains(PortRulesPostMortem.RaiseSettingAdvice(PortRulesPostMortem.ClosedLoopMillCeiling), text);
        Assert.Contains(PortRulesPostMortem.MillBreakAdvice, text);
        Assert.DoesNotContain("That is not a closed read/edit/test loop", text);
    }

    [Fact]
    public void Rapid_churn_names_Rapid_churn_consecutive()
    {
        var rules = PortForwardingRules.Defaults with { RapidChurnConsecutive = 4, RapidChurnSeconds = 3 };
        var text = PortRulesPostMortem.FormatMill(40, rapidChurn: true, rapidStreak: 4, rules);
        Assert.Contains("the Client app sent tools too quickly", text);
        Assert.Contains("4 turns in a row", text);
        Assert.Contains("**Rapid-churn consecutive**", text);
        Assert.Contains(PortRulesPostMortem.RaiseSettingAdvice(PortRulesPostMortem.RapidChurnConsecutive), text);
        Assert.Contains(PortRulesPostMortem.PortRulesLocation, text);
        Assert.Contains(PortRulesPostMortem.NarrowerChatAdvice, text);
        Assert.Contains("**Rapid-churn seconds**", text);
        Assert.DoesNotContain(PortRulesPostMortem.MillBreakAdvice, text);
        Assert.DoesNotContain(PortRulesPostMortem.SmallerNextStepAdvice, text);
    }

    [Fact]
    public void Hang_first_byte_names_First_byte_seconds_and_asks_for_one_clear_outcome()
    {
        var text = PortRulesPostMortem.FormatHang(
            LocalStreamHangPolicy.FirstByteReason,
            quietSeconds: 25,
            waitLongerCount: 0,
            firstByteDeadlineSeconds: 25,
            PortForwardingRules.Defaults);
        Assert.Contains("llama-server did not start a reply in time", text);
        Assert.Contains("**First-byte seconds**", text);
        Assert.Contains("the current setting is 25", text);
        Assert.Contains(PortRulesPostMortem.RaiseSettingAdvice(PortRulesPostMortem.FirstByteSeconds), text);
        Assert.Contains(PortRulesPostMortem.PortRulesLocation, text);
        Assert.Contains("**Max think first-byte**", text);
        Assert.Contains(PortRulesPostMortem.NarrowerChatAdvice, text);
    }

    [Fact]
    public void Hang_after_wait_longer_names_Wait_longer_seconds()
    {
        var text = PortRulesPostMortem.FormatHang(
            LocalStreamHangPolicy.FirstByteReason,
            quietSeconds: 90,
            waitLongerCount: 1,
            firstByteDeadlineSeconds: 90,
            PortForwardingRules.Defaults);
        Assert.Contains("you chose to keep this turn open", text);
        Assert.Contains("**Wait-longer seconds**", text);
        Assert.Contains("the current setting is 90", text);
        Assert.Contains(PortRulesPostMortem.NarrowerChatAdvice, text);
    }

    [Fact]
    public void Hang_stall_names_Stall_seconds()
    {
        var text = PortRulesPostMortem.FormatHang(
            LocalStreamHangPolicy.StallReason,
            quietSeconds: 45,
            waitLongerCount: 0,
            firstByteDeadlineSeconds: 25,
            PortForwardingRules.Defaults);
        Assert.Contains("llama-server went quiet in the middle of a reply", text);
        Assert.Contains("**Stall seconds**", text);
        Assert.Contains("the current setting is 45", text);
        Assert.Contains(PortRulesPostMortem.RaiseSettingAdvice(PortRulesPostMortem.StallSeconds), text);
        Assert.Contains(PortRulesPostMortem.PortRulesLocation, text);
        Assert.Contains(PortRulesPostMortem.NarrowerChatAdvice, text);
    }

    [Fact]
    public void Loading_503_names_retries_and_delay()
    {
        var text = PortRulesPostMortem.FormatLoading503(6, PortForwardingRules.Defaults);
        Assert.Contains("llama-server was still loading and did not take this turn", text);
        Assert.Contains("**503 retries**", text);
        Assert.Contains("the current setting is 6", text);
        Assert.Contains("503 delay seconds", text);
        Assert.Contains(PortRulesPostMortem.PortRulesLocation, text);
        Assert.DoesNotContain(PortRulesPostMortem.NarrowerAskAdvice, text);
        Assert.DoesNotContain(PortRulesPostMortem.SmallerNextStepAdvice, text);
    }

    [Fact]
    public void Think_only_names_Stop_strings_and_asks_for_one_clear_outcome()
    {
        var text = PortRulesPostMortem.FormatThinkOnly(PortForwardingRules.Defaults);
        Assert.Contains("llama-server only thought", text);
        Assert.Contains("Stop strings", text);
        Assert.Contains("Ignore Client-app stop", text);
        Assert.Contains(PortRulesPostMortem.PortRulesLocation, text);
        Assert.Contains(PortRulesPostMortem.EnableReasoningAdvice, text);
        Assert.Contains(PortRulesPostMortem.NarrowerChatAdvice, text);
    }

    [Fact]
    public void Context_overflow_names_Headroom_and_says_Compact_cannot_fix_this_turn()
    {
        var text = PortRulesPostMortem.FormatContextOverflow(compactApplied: true, PortForwardingRules.Defaults);
        Assert.Contains("this turn was too large for the loaded Context", text);
        Assert.Contains("**Headroom**", text);
        Assert.Contains("**Watermark %**", text);
        Assert.Contains("Compact cannot shorten one over-full Client-app message", text);
        Assert.Contains(PortRulesPostMortem.NoPortRuleForOverflow, text);
        Assert.Contains(PortRulesPostMortem.PortRulesLocation, text);
        Assert.Contains(PortRulesPostMortem.RaiseContextAdvice, text);
        Assert.Contains(PortRulesPostMortem.SmallerNextStepAdvice, text);
        Assert.DoesNotContain("**Temperature**", text);
    }

    [Fact]
    public void Mill_at_omitted_client_error_uses_the_shared_stop_shape()
    {
        var text = FluxMuxGatewayRouting.FormatToolMillMessage(
            null,
            omitted: 32,
            rapidChurn: false,
            rapidStreak: 0,
            PortForwardingRules.Defaults);
        Assert.Contains("You already passed", text);
        Assert.Contains("**Mill at omitted**", text);
        Assert.Contains("**Closed-loop lookback**", text);
        Assert.Contains("**Closed-loop mill ceiling**", text);
        Assert.Contains(PortRulesPostMortem.PortRuleStopAdvice, text);
        Assert.DoesNotContain(PortRulesPostMortem.RaiseSettingAdvice(PortRulesPostMortem.MillAtOmitted), text);
    }

    [Fact]
    public void Client_errors_name_the_tripped_Port_rule_and_where_to_change_it()
    {
        var mill = FluxMuxGatewayRouting.FormatToolMillMessage(null, 0, false, 0, PortForwardingRules.Defaults);
        Assert.Contains("Observe-only mill", mill);
        Assert.Contains(PortRulesPostMortem.PortRulesLocation, mill);

        var hang = LocalStreamHangPolicy.FormatHangAbortMessage(
            null,
            PortRulesPostMortem.FormatHang(
                LocalStreamHangPolicy.FirstByteReason,
                quietSeconds: 25,
                waitLongerCount: 0,
                firstByteDeadlineSeconds: 25,
                PortForwardingRules.Defaults));
        Assert.Contains("First-byte seconds", hang);
        Assert.Contains(PortRulesPostMortem.PortRulesLocation, hang);

        var think = FluxMuxGatewayRouting.FormatThinkOnlyMessage(null, PortForwardingRules.Defaults);
        Assert.Contains("Stop strings", think);
        Assert.Contains(PortRulesPostMortem.PortRulesLocation, think);

        var overflow = FluxMuxGatewayRouting.FormatLocalContextOverflowMessage(
            null,
            portRuleDetails: PortRulesPostMortem.FormatContextOverflow(compactApplied: true, PortForwardingRules.Defaults));
        Assert.Contains("Headroom", overflow);
        Assert.Contains(PortRulesPostMortem.PortRulesLocation, overflow);

        var loading = PortRulesPostMortem.JoinClientError(
            "llama-server is still loading.",
            PortRulesPostMortem.FormatLoading503(6, PortForwardingRules.Defaults));
        Assert.Contains("503 retries", loading);
        Assert.Contains(PortRulesPostMortem.PortRulesLocation, loading);
    }

    [Fact]
    public void Repeated_command_and_diagnostic_dump_name_the_Client_app_and_no_Port_rule()
    {
        var repeated = PortRulesPostMortem.FormatRepeatedCommand();
        Assert.Contains("the Client app ran the same command twice", repeated);
        Assert.Contains(PortRulesPostMortem.ClientAppFaultLead, repeated);
        Assert.Contains(PortRulesPostMortem.MillBreakAdvice, repeated);
        Assert.StartsWith(PortRulesPostMortem.ChatTurnCannotContinue, repeated);

        var dump = PortRulesPostMortem.FormatDiagnosticDump(6);
        Assert.Contains("the Client app created 6 session diagnostic files", dump);
        Assert.Contains(PortRulesPostMortem.ClientAppFaultLead, dump);
        Assert.Contains(PortRulesPostMortem.NarrowerChatAdvice, dump);
        Assert.StartsWith(PortRulesPostMortem.ChatTurnCannotContinue, dump);
    }

    [Fact]
    public void Steer_notice_and_pause_prompt_do_not_end_the_turn_or_say_HTTP_400()
    {
        var mill = PortRulesPostMortem.FormatMill(32, rapidChurn: false, rapidStreak: 0, PortForwardingRules.Defaults);
        var dump = PortRulesPostMortem.FormatDiagnosticDump(6);
        var repeated = PortRulesPostMortem.FormatRepeatedCommand();
        foreach (var finding in new[] { mill, dump, repeated })
        {
            var prompt = PortRulesPostMortem.FormatPausePrompt(finding);
            Assert.False(string.IsNullOrWhiteSpace(prompt));
            Assert.DoesNotContain(PortRulesPostMortem.PortRuleStopAdvice, prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("400", prompt, StringComparison.Ordinal);
            Assert.False(prompt.StartsWith(PortRulesPostMortem.ChatTurnCannotContinue, StringComparison.Ordinal));

            var steer = PortRulesPostMortem.FormatSteerNotice(finding);
            Assert.Contains(PortRulesPostMortem.SteerNoticeLead.Trim(), steer, StringComparison.Ordinal);
            Assert.Contains(PortRulesPostMortem.SteerNoticeCloser, steer, StringComparison.Ordinal);
            Assert.DoesNotContain(PortRulesPostMortem.PortRuleStopAdvice, steer, StringComparison.Ordinal);
            Assert.DoesNotContain("400", steer, StringComparison.Ordinal);
            Assert.DoesNotContain("This Client-app turn is over", steer, StringComparison.Ordinal);
        }

        Assert.Contains(PortRulesPostMortem.PortRuleStopAdvice, FluxMuxGatewayRouting.FormatToolMillMessage(null), StringComparison.Ordinal);
        Assert.Contains(PortRulesPostMortem.PortRuleStopAdvice, FluxMuxGatewayRouting.FormatRepeatedToolMessage(null), StringComparison.Ordinal);
        Assert.Contains(PortRulesPostMortem.PortRuleStopAdvice, LocalSessionArtifactPolicy.FormatHaltMessage(null), StringComparison.Ordinal);
    }
}
