using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class RouteRecoveryPolicyTests
{
    [Theory]
    [InlineData("quota_exhausted", true)]
    [InlineData("rate_limited", true)]
    [InlineData("request_timeout", true)]
    [InlineData("upstream_overloaded", true)]
    [InlineData("auth_failed", true)]
    [InlineData("model_not_found", true)]
    [InlineData("model_not_supported", true)]
    [InlineData("connection_reset", true)]
    [InlineData("cloud_vision_unavailable", true)]
    [InlineData("invalid_request_error", false)]
    [InlineData("exceed_context_size_error", false)]
    public void Cloud_failures_that_should_offer_a_switch(string errorType, bool expected)
    {
        Assert.Equal(expected, FluxMuxUpstreamErrorClassifier.ShouldOfferRouteSwitch(errorType));
    }

    [Theory]
    [InlineData("auth_failed")]
    [InlineData("model_not_found")]
    [InlineData("model_not_supported")]
    [InlineData("connection_reset")]
    public void Auth_and_missing_model_offer_a_switch_without_opening_the_breaker(string errorType)
    {
        Assert.True(FluxMuxUpstreamErrorClassifier.ShouldOfferRouteSwitch(errorType));
        Assert.False(FluxMuxUpstreamErrorClassifier.ShouldOpenCloudBreaker(errorType));
    }

    [Fact]
    public void Cloud_failure_copy_is_model_neutral()
    {
        var text = FluxMuxUpstreamErrorClassifier.FormatCloudFailureRecoveryReason(
            "Gemini",
            "gemini-flash",
            "quota_exhausted");

        Assert.Contains("token budget", text, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("This chat turn is over", text, System.StringComparison.Ordinal);
        Assert.Contains(RouteRecoveryPolicy.ResumeWaitingLabel, text, System.StringComparison.Ordinal);
        Assert.Contains(ControlLabelMarkup.Mark(RouteRecoveryPolicy.ResumeWaitingLabel), text, System.StringComparison.Ordinal);
        Assert.Contains(ControlLabelMarkup.Mark(RouteRecoveryPolicy.SwitchLabelPrefix), text, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Use cloud", text, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Stay local", text, System.StringComparison.Ordinal);
        Assert.DoesNotContain("retry", text, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Switch_label_uses_the_other_model_name()
    {
        Assert.Equal("Switch to Gemini / gemini-flash", RouteRecoveryPolicy.FormatSwitchLabel("Gemini / gemini-flash"));
        Assert.Equal(
            "Switch to Gemini / gemini-flash" + RouteRecoveryPolicy.SwitchHarnessRelaunchSuffix,
            RouteRecoveryPolicy.FormatSwitchLabel("Gemini / gemini-flash", harnessNeedsRelaunch: true));
        Assert.Equal(RouteRecoveryPolicy.ResumeWaitingLabel, "Keep current model — end this turn");
        Assert.Equal(RouteRecoveryPolicy.WaitLongerLabel, "Continue waiting");
        Assert.Contains("reconnecting", RouteRecoveryPolicy.HarnessRelaunchHint, System.StringComparison.Ordinal);
        Assert.Contains("Client app", RouteRecoveryPolicy.HarnessRelaunchHint, System.StringComparison.Ordinal);
        Assert.False(RouteRecoveryPolicy.ShouldShowHarnessRelaunchHint(true, continueWaitingOffered: true));
        Assert.False(RouteRecoveryPolicy.ShouldShowHarnessRelaunchHint(
            true,
            continueWaitingOffered: false,
            RouteRecoveryPolicy.CapacitySource));
        Assert.True(RouteRecoveryPolicy.ShouldShowHarnessRelaunchHint(
            true,
            continueWaitingOffered: false,
            RouteRecoveryPolicy.CloudFailSource));
        Assert.False(RouteRecoveryPolicy.ShouldShowHarnessRelaunchHint(
            true,
            continueWaitingOffered: false,
            RouteRecoveryPolicy.LocalCannotSource));
        Assert.False(RouteRecoveryPolicy.ShouldShowCapacityPrompt(cloudReady: false));
        Assert.True(RouteRecoveryPolicy.ShouldShowCapacityPrompt(cloudReady: true));
        Assert.Equal(RouteRecoveryPolicy.StayLocalContinueLabel, RouteRecoveryPolicy.FormatResumeLabel(RouteRecoveryPolicy.CapacitySource));
        Assert.Equal(RouteRecoveryPolicy.StayLocalContinueLabel, RouteRecoveryPolicy.FormatResumeLabel(RouteRecoveryPolicy.LocalCannotSource));
        Assert.Equal(RouteRecoveryPolicy.StayLocalContinueLabel, RouteRecoveryPolicy.FormatResumeLabel(CloudReturnToLocalPolicy.Source));
        Assert.Equal(RouteRecoveryPolicy.ResumeWaitingLabel, RouteRecoveryPolicy.FormatResumeLabel(RouteRecoveryPolicy.LocalHangSource));
        Assert.False(RouteRecoveryPolicy.ShouldShowHarnessRelaunchHint(
            true,
            continueWaitingOffered: false,
            CloudReturnToLocalPolicy.Source));
        Assert.True(RouteRecoveryPolicy.IsHotHopSource(RouteRecoveryPolicy.CapacitySource));
        Assert.True(RouteRecoveryPolicy.IsHotHopSource(RouteRecoveryPolicy.LocalCannotSource));
        Assert.True(RouteRecoveryPolicy.IsHotHopSource(CloudReturnToLocalPolicy.Source));
        Assert.False(RouteRecoveryPolicy.IsHotHopSource(RouteRecoveryPolicy.CloudFailSource));
        Assert.False(RouteRecoveryPolicy.IsHotHopSource(RouteRecoveryPolicy.ToolLoopSource));
        Assert.True(RouteRecoveryPolicy.IsToolLoopSource(RouteRecoveryPolicy.ToolLoopSource));
        Assert.False(RouteRecoveryPolicy.IsLocalHangSource(RouteRecoveryPolicy.ToolLoopSource));
        Assert.Equal(RouteRecoveryPolicy.ResumeWaitingLabel, RouteRecoveryPolicy.FormatResumeLabel(RouteRecoveryPolicy.ToolLoopSource));
        Assert.Equal(RouteRecoveryPolicy.EndThisTurnLabel, RouteRecoveryPolicy.FormatResumeLabel(RouteRecoveryPolicy.PortRulePauseSource));
        Assert.Equal(RouteRecoveryPolicy.SendAnywayLabel, RouteRecoveryPolicy.FormatWaitLongerLabel(RouteRecoveryPolicy.PortRulePauseSource));
        Assert.Equal(RouteRecoveryPolicy.WaitLongerLabel, RouteRecoveryPolicy.FormatWaitLongerLabel(RouteRecoveryPolicy.LocalHangSource));
        Assert.True(RouteRecoveryPolicy.IsPortRulePauseSource(RouteRecoveryPolicy.PortRulePauseSource));
        Assert.False(RouteRecoveryPolicy.IsPortRulePauseSource(RouteRecoveryPolicy.ToolLoopSource));
        Assert.False(RouteRecoveryPolicy.IsPortRulePauseSource(RouteRecoveryPolicy.LocalHangSource));
        Assert.Contains("llama-server", RouteRecoveryPolicy.FormatWaitLongerTooltip(RouteRecoveryPolicy.PortRulePauseSource), System.StringComparison.Ordinal);
        Assert.DoesNotContain("400", RouteRecoveryPolicy.FormatWaitLongerTooltip(RouteRecoveryPolicy.PortRulePauseSource), System.StringComparison.Ordinal);
        Assert.DoesNotContain("400", RouteRecoveryPolicy.FormatResumeTooltip("Cline", turnContinues: false, RouteRecoveryPolicy.PortRulePauseSource), System.StringComparison.Ordinal);
        Assert.DoesNotContain("400", RouteRecoveryPolicy.FormatSteerTooltip(), System.StringComparison.Ordinal);
        Assert.False(RouteRecoveryPolicy.IsTimeoutStatus(RouteRecoveryPolicy.WaitStatus));
        Assert.False(RouteRecoveryPolicy.IsTimeoutStatus(RouteRecoveryPolicy.SteerStatus));
        Assert.True(RouteRecoveryPolicy.IsWaitStatus(RouteRecoveryPolicy.WaitStatus));
        Assert.True(RouteRecoveryPolicy.IsSteerStatus(RouteRecoveryPolicy.SteerStatus));
        Assert.Contains("llama-server", FluxMuxGatewayRouting.FormatRepeatedToolConsentReason("npm test"), System.StringComparison.Ordinal);
        Assert.Contains("Client app", FluxMuxGatewayRouting.FormatRepeatedToolMessage(null), System.StringComparison.Ordinal);
        Assert.Contains("Client app", FluxMuxGatewayRouting.FormatToolMillMessage(null), System.StringComparison.Ordinal);
        Assert.Contains("**Observe-only mill**", FluxMuxGatewayRouting.FormatToolMillMessage(null), System.StringComparison.Ordinal);
        Assert.Contains("Observe-only mill", FluxMuxGatewayRouting.FormatToolMillMessage(null), System.StringComparison.Ordinal);
        Assert.Contains(
            PortRulesPostMortem.PortRulesLocation,
            FluxMuxGatewayRouting.FormatToolMillMessage(null),
            System.StringComparison.Ordinal);
        Assert.Contains(
            "Another message in this chat will hit the same stop",
            FluxMuxGatewayRouting.FormatToolMillMessage(null),
            System.StringComparison.Ordinal);
        Assert.DoesNotContain(
            "next message in this Client-app chat",
            FluxMuxGatewayRouting.FormatToolMillMessage(null),
            System.StringComparison.Ordinal);
        Assert.DoesNotContain(
            "would repeat",
            FluxMuxGatewayRouting.FormatToolMillMessage(null),
            System.StringComparison.Ordinal);
        Assert.DoesNotContain("the runner", FluxMuxGatewayRouting.FormatRepeatedToolMessage(null), System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hang_wait_copy_offers_continue_waiting()
    {
        Assert.Contains(RouteRecoveryPolicy.WaitLongerLabel, LocalStreamHangPolicy.HangWaitMessage, System.StringComparison.Ordinal);
        Assert.Contains(RouteRecoveryPolicy.ResumeWaitingLabel, LocalStreamHangPolicy.HangWaitMessage, System.StringComparison.Ordinal);
        Assert.Contains(ControlLabelMarkup.Mark(RouteRecoveryPolicy.WaitLongerLabel), LocalStreamHangPolicy.HangWaitMessage, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Keep or Switch", LocalStreamHangPolicy.HangWaitMessage, System.StringComparison.Ordinal);
        Assert.Contains("same choices will appear again", LocalStreamHangPolicy.HangWaitMessage, System.StringComparison.Ordinal);
        Assert.DoesNotContain("retry", LocalStreamHangPolicy.HangWaitMessage, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("keep this turn open", LocalStreamHangPolicy.HangWaitMessage, System.StringComparison.Ordinal);
        Assert.Contains("Client app", LocalStreamHangPolicy.HangWaitMessage, System.StringComparison.Ordinal);
        Assert.Contains("'reconnecting'", LocalStreamHangPolicy.HangWaitMessage, System.StringComparison.Ordinal);
        Assert.Contains("reconnecting", LocalStreamHangPolicy.HangWaitMessage, System.StringComparison.Ordinal);
        Assert.Contains(PortRulesPostMortem.PortRuleStopAdvice, LocalStreamHangPolicy.HangWaitMessage, System.StringComparison.Ordinal);
        Assert.DoesNotContain("The same model is fine", LocalStreamHangPolicy.HangWaitMessage, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Hang_wait_copy_covers_Cline_and_Harness_together()
    {
        var text = LocalStreamHangPolicy.FormatHangWaitMessage(FluxMuxGatewayRouting.HarnessEndpointApp);
        Assert.Equal(text, LocalStreamHangPolicy.FormatHangWaitMessage("Cline"));
        Assert.Contains("not the Client app", text, System.StringComparison.Ordinal);
        Assert.Contains("The Client app may keep working", text, System.StringComparison.Ordinal);
        Assert.Contains("this prompt closes if llama-server starts sending", text, System.StringComparison.Ordinal);
        Assert.Contains("reconnecting", text, System.StringComparison.Ordinal);
        Assert.Contains("'reconnecting'", text, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Cline", text, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Harness", text, System.StringComparison.Ordinal);
        Assert.Contains(ControlLabelMarkup.Mark(RouteRecoveryPolicy.WaitLongerLabel), text, System.StringComparison.Ordinal);
        Assert.Contains(ControlLabelMarkup.Mark(RouteRecoveryPolicy.ResumeWaitingLabel), text, System.StringComparison.Ordinal);
        Assert.Contains(ControlLabelMarkup.Mark(RouteRecoveryPolicy.SwitchLabelPrefix), text, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Keep or Switch", text, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Context bar", text, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hang_abort_copy_says_start_a_fresh_chat_not_add_to_this_one()
    {
        Assert.Contains("This chat turn cannot continue", LocalStreamHangPolicy.HangAbortMessage, System.StringComparison.Ordinal);
        Assert.Contains("did not start a reply in time", LocalStreamHangPolicy.HangAbortMessage, System.StringComparison.Ordinal);
        Assert.Contains("'reconnecting'", LocalStreamHangPolicy.HangAbortMessage, System.StringComparison.Ordinal);
        Assert.Contains("reconnecting", LocalStreamHangPolicy.HangAbortMessage, System.StringComparison.Ordinal);
        Assert.Contains(PortRulesPostMortem.PortRuleStopAdvice, LocalStreamHangPolicy.HangAbortMessage, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Cline", LocalStreamHangPolicy.HangAbortMessage, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Harness", LocalStreamHangPolicy.HangAbortMessage, System.StringComparison.Ordinal);
        Assert.DoesNotContain("This Cline turn is over", LocalStreamHangPolicy.HangAbortMessage, System.StringComparison.Ordinal);
        Assert.DoesNotContain("The same model is fine", LocalStreamHangPolicy.HangAbortMessage, System.StringComparison.Ordinal);
        Assert.Equal(
            LocalStreamHangPolicy.HangAbortMessage,
            LocalStreamHangPolicy.FormatHangAbortMessage(FluxMuxGatewayRouting.HarnessEndpointApp));
        Assert.Equal(
            LocalStreamHangPolicy.HangAbortMessage,
            LocalStreamHangPolicy.FormatHangAbortMessage("Cline"));
        Assert.DoesNotContain("packed Context", LocalStreamHangPolicy.HangAbortMessage, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Keep the current model", LocalStreamHangPolicy.HangAbortMessage, System.StringComparison.Ordinal);
        Assert.DoesNotContain("retry", LocalStreamHangPolicy.HangAbortMessage, System.StringComparison.OrdinalIgnoreCase);
        Assert.False(RouteRecoveryPolicy.IsLocalHangSource(RouteRecoveryPolicy.LocalHangAbortSource));
        Assert.True(RouteRecoveryPolicy.IsLocalHangAbortSource(RouteRecoveryPolicy.LocalHangAbortSource));
    }

    [Fact]
    public void Timed_out_ram_spill_consent_does_not_keep_the_popup()
    {
        Assert.False(RouteRecoveryPolicy.ShouldKeepTimedOutCloudRecommend(failOnTimeout: false));
        Assert.True(RouteRecoveryPolicy.ShouldKeepTimedOutCloudRecommend(failOnTimeout: true));
        Assert.True(RouteRecoveryPolicy.IsTimeoutStatus("timeout"));
    }

    [Fact]
    public void Filling_turn_holds_for_switch_to_cloud_instead_of_skipping_to_load_suggested()
    {
        Assert.True(RouteRecoveryPolicy.HoldInFlightRequestForCloudConsent());
        Assert.False(RouteRecoveryPolicy.PreferLocalReloadOverCloudConsent(
            true,
            FluxMuxGatewayRouting.FillingCloudConsentReason));
        Assert.False(RouteRecoveryPolicy.PreferLocalReloadOverCloudConsent(
            true,
            FluxMuxGatewayRouting.CapacityCloudConsentReason));
    }
}
