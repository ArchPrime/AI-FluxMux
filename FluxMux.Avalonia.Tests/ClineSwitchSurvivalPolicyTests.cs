using System;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class ClineSwitchSurvivalPolicyTests
{
    [Fact]
    public void Hot_hop_to_a_ready_target_keeps_this_Cline_turn()
    {
        Assert.True(ClineSwitchSurvivalPolicy.ThisTurnSurvives(
            targetAlreadyReady: true,
            llamaServerMustReload: false,
            thisTurnAlreadyFailed: false));
        Assert.Equal(
            RecoveryChatFollowThrough.ContinueThisChat,
            ClineSwitchSurvivalPolicy.Decide(
                harnessEndpoint: false,
                targetAlreadyReady: true,
                llamaServerMustReload: false,
                thisTurnAlreadyFailed: false));
    }

    [Fact]
    public void Llama_server_reload_ends_this_Cline_turn_but_not_the_task()
    {
        Assert.False(ClineSwitchSurvivalPolicy.ThisTurnSurvives(
            targetAlreadyReady: false,
            llamaServerMustReload: true,
            thisTurnAlreadyFailed: false));
        Assert.True(ClineSwitchSurvivalPolicy.SameTaskNextMessageCanUseNewModel());
        Assert.Equal(
            RecoveryChatFollowThrough.ContinueSameTaskNextMessage,
            ClineSwitchSurvivalPolicy.Decide(
                harnessEndpoint: false,
                targetAlreadyReady: false,
                llamaServerMustReload: true,
                thisTurnAlreadyFailed: false));
    }

    [Fact]
    public void Failed_400_on_Cline_can_retry_this_task_after_a_ready_replacement()
    {
        Assert.False(ClineSwitchSurvivalPolicy.ThisTurnSurvives(
            targetAlreadyReady: true,
            llamaServerMustReload: false,
            thisTurnAlreadyFailed: true));
        Assert.Equal(
            RecoveryChatFollowThrough.ContinueSameTaskNextMessage,
            ClineSwitchSurvivalPolicy.Decide(
                harnessEndpoint: false,
                targetAlreadyReady: true,
                llamaServerMustReload: false,
                thisTurnAlreadyFailed: true));
        Assert.Contains(
            "next message in this task",
            ClineSwitchSurvivalPolicy.SameTaskAfterFailedTurnAdvice,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Do not try to continue this chat",
            ClineSwitchSurvivalPolicy.SameTaskAfterFailedTurnAdvice,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Reload_or_failed_turn_uses_same_task_next_message_even_if_Harness_is_also_open()
    {
        Assert.Equal(
            RecoveryChatFollowThrough.ContinueSameTaskNextMessage,
            ClineSwitchSurvivalPolicy.Decide(
                harnessEndpoint: true,
                targetAlreadyReady: false,
                llamaServerMustReload: true,
                thisTurnAlreadyFailed: false));
        Assert.Equal(
            RecoveryChatFollowThrough.ContinueThisChat,
            ClineSwitchSurvivalPolicy.Decide(
                harnessEndpoint: true,
                targetAlreadyReady: true,
                llamaServerMustReload: false,
                thisTurnAlreadyFailed: false));
    }

    [Fact]
    public void Failed_turn_advice_covers_Cline_and_Harness_together()
    {
        var harness = ClineSwitchSurvivalPolicy.FormatFailedTurnAdvice(
            FluxMuxGatewayRouting.HarnessEndpointApp);
        var cline = ClineSwitchSurvivalPolicy.FormatFailedTurnAdvice("Cline");
        Assert.Equal(cline, harness);
        Assert.Contains("Cline", harness, StringComparison.Ordinal);
        Assert.Contains("If Harness is also using Port", harness, StringComparison.Ordinal);
        Assert.Contains(
            "If Harness is also using Port",
            ClineSwitchSurvivalPolicy.FormatReloadAdvice(FluxMuxGatewayRouting.HarnessEndpointApp),
            StringComparison.Ordinal);
        Assert.Contains(
            "next message in this task",
            ClineSwitchSurvivalPolicy.FormatReloadAdvice("Cline"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Swap_dialog_tooltips_cover_Cline_and_Harness_together()
    {
        var resume = RouteRecoveryPolicy.FormatResumeTooltip("Cline", turnContinues: false);
        Assert.Equal(
            resume,
            RouteRecoveryPolicy.FormatResumeTooltip(
                FluxMuxGatewayRouting.HarnessEndpointApp,
                turnContinues: false));
        Assert.Contains("Cline", resume, StringComparison.Ordinal);
        Assert.Contains("new Cline task", resume, StringComparison.Ordinal);
        Assert.Contains("If Harness is also using Port", resume, StringComparison.Ordinal);

        var clineSwitch = RouteRecoveryPolicy.FormatSwitchTooltip("Cline", cloud: true, turnContinues: false);
        Assert.Contains("next message in this task", clineSwitch, StringComparison.Ordinal);
        Assert.Contains("If Harness is also using Port", clineSwitch, StringComparison.Ordinal);

        var hotHop = RouteRecoveryPolicy.FormatSwitchTooltip("Cline", cloud: false, turnContinues: true);
        Assert.Contains("can continue", hotHop, StringComparison.Ordinal);
        Assert.DoesNotContain("Harness", hotHop, StringComparison.Ordinal);
        Assert.Contains(
            "If Harness is also using Port",
            RouteRecoveryPolicy.FormatKeepCurrentStatus("Cline"),
            StringComparison.Ordinal);
    }
}
