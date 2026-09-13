using System;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class RecoveryReplacementPolicyTests
{
    [Fact]
    public void Ready_cloud_on_an_open_ram_spill_turn_can_continue_this_chat()
    {
        var follow = RecoveryReplacementPolicy.Decide(
            FluxMuxGatewayRouting.HarnessEndpointApp,
            new RecoveryReplacementChoice(
                IsCloud: true,
                LlamaServerMustReload: false,
                TurnStillOpen: true));
        Assert.Equal(RecoveryChatFollowThrough.ContinueThisChat, follow);
        Assert.Contains("can continue", RecoveryReplacementPolicy.FormatAdvice(follow), StringComparison.Ordinal);
    }

    [Fact]
    public void Overlay_local_on_an_open_turn_can_continue_this_chat()
    {
        var follow = RecoveryReplacementPolicy.Decide(
            "Cline",
            new RecoveryReplacementChoice(
                IsCloud: false,
                LlamaServerMustReload: false,
                TurnStillOpen: true));
        Assert.Equal(RecoveryChatFollowThrough.ContinueThisChat, follow);
    }

    [Fact]
    public void Launch_critical_local_switch_keeps_the_Cline_task_and_notes_Harness_if_open()
    {
        var follow = RecoveryReplacementPolicy.Decide(
            FluxMuxGatewayRouting.HarnessEndpointApp,
            new RecoveryReplacementChoice(
                IsCloud: false,
                LlamaServerMustReload: true,
                TurnStillOpen: true));
        Assert.Equal(RecoveryChatFollowThrough.ContinueSameTaskNextMessage, follow);
        Assert.Contains("next message in this Client-app chat", RecoveryReplacementPolicy.FormatAdvice(follow), StringComparison.Ordinal);
        Assert.Contains("reconnecting", RecoveryReplacementPolicy.FormatAdvice(follow), StringComparison.Ordinal);
        Assert.Contains("Client app", RecoveryReplacementPolicy.FormatAdvice(follow), StringComparison.Ordinal);
    }

    [Fact]
    public void Ended_filling_turn_on_Cline_can_retry_this_task_after_the_new_model_is_ready()
    {
        var follow = RecoveryReplacementPolicy.Decide(
            "Cline",
            new RecoveryReplacementChoice(
                IsCloud: false,
                LlamaServerMustReload: true,
                TurnStillOpen: false));
        Assert.Equal(RecoveryChatFollowThrough.ContinueSameTaskNextMessage, follow);
        Assert.Contains("next message in this Client-app chat", RecoveryReplacementPolicy.FormatAdvice(follow), StringComparison.Ordinal);
        Assert.Contains("llama-server reload", RecoveryReplacementPolicy.FormatAdvice(follow), StringComparison.Ordinal);
        Assert.Contains("reconnecting", RecoveryReplacementPolicy.FormatAdvice(follow), StringComparison.Ordinal);
        Assert.DoesNotContain("Do not try to continue this chat", RecoveryReplacementPolicy.FormatAdvice(follow), StringComparison.Ordinal);
    }

    [Fact]
    public void Return_to_local_waiting_turn_counts_as_still_open()
    {
        Assert.True(RecoveryReplacementPolicy.TurnStillOpen(
            cloudConsentWaiting: true,
            failOnTimeout: false,
            cloudSource: CloudReturnToLocalPolicy.Source));
    }

    [Fact]
    public void Turn_still_open_is_any_hot_hop_consent_that_is_waiting()
    {
        Assert.True(RecoveryReplacementPolicy.TurnStillOpen(
            cloudConsentWaiting: true,
            failOnTimeout: false,
            cloudSource: RouteRecoveryPolicy.CapacitySource));
        Assert.True(RecoveryReplacementPolicy.TurnStillOpen(
            cloudConsentWaiting: true,
            failOnTimeout: true,
            cloudSource: RouteRecoveryPolicy.CapacitySource));
        Assert.True(RecoveryReplacementPolicy.TurnStillOpen(
            cloudConsentWaiting: true,
            failOnTimeout: true,
            cloudSource: RouteRecoveryPolicy.LocalCannotSource));
        Assert.False(RecoveryReplacementPolicy.TurnStillOpen(
            cloudConsentWaiting: true,
            failOnTimeout: false,
            cloudSource: RouteRecoveryPolicy.LocalHangAbortSource));
        Assert.False(RecoveryReplacementPolicy.TurnStillOpen(
            cloudConsentWaiting: false,
            failOnTimeout: false,
            cloudSource: RouteRecoveryPolicy.CapacitySource));
    }

    [Fact]
    public void Harness_local_reload_opens_harness_from_the_new_slot()
    {
        Assert.True(RecoveryReplacementPolicy.ShouldOpenHarnessAfterLocalSwap(
            harnessEndpoint: true,
            llamaServerMustReload: true));
        Assert.False(RecoveryReplacementPolicy.ShouldOpenHarnessAfterLocalSwap(
            harnessEndpoint: true,
            llamaServerMustReload: false));
        Assert.False(RecoveryReplacementPolicy.ShouldOpenHarnessAfterLocalSwap(
            harnessEndpoint: false,
            llamaServerMustReload: true));
    }

    [Fact]
    public void Hot_cloud_or_hot_local_does_not_open_a_new_harness_tab()
    {
        Assert.False(RecoveryReplacementPolicy.ShouldOpenHarnessAfterPromptedLaunch(
            harnessEndpoint: true,
            launchedNewRuntime: false));
        var hotCloud = RecoveryReplacementPolicy.Decide(
            FluxMuxGatewayRouting.HarnessEndpointApp,
            new RecoveryReplacementChoice(
                IsCloud: true,
                LlamaServerMustReload: false,
                TurnStillOpen: true,
                CloudAlreadyReady: true));
        Assert.Equal(RecoveryChatFollowThrough.ContinueThisChat, hotCloud);
    }

    [Fact]
    public void Chosen_cold_cloud_is_not_the_hot_cloud()
    {
        Assert.True(RecoveryReplacementPolicy.CloudPickIsAlreadyHot(
            cloudRouteActive: true,
            mountedProvider: "FluxMux Stub",
            mountedModel: "stub-cloud",
            pickProvider: "FluxMux Stub",
            pickModel: "stub-cloud"));
        Assert.False(RecoveryReplacementPolicy.CloudPickIsAlreadyHot(
            cloudRouteActive: true,
            mountedProvider: "FluxMux Stub",
            mountedModel: "stub-cloud",
            pickProvider: "Gemini",
            pickModel: "gemini-flash"));
        Assert.False(RecoveryReplacementPolicy.CloudPickIsAlreadyHot(
            cloudRouteActive: false,
            mountedProvider: "",
            mountedModel: "",
            pickProvider: "Gemini",
            pickModel: "gemini-flash"));
    }

    [Fact]
    public void Hop_to_any_cloud_keeps_this_Harness_chat()
    {
        Assert.False(RecoveryReplacementPolicy.ShouldOpenHarnessAfterCloudLaunch(
            harnessEndpoint: true,
            launchedNewCloud: true,
            localStillAlive: true));
        Assert.False(RecoveryReplacementPolicy.ShouldOpenHarnessAfterCloudLaunch(
            harnessEndpoint: true,
            launchedNewCloud: true,
            localStillAlive: false));
        var coldCloud = RecoveryReplacementPolicy.Decide(
            FluxMuxGatewayRouting.HarnessEndpointApp,
            new RecoveryReplacementChoice(
                IsCloud: true,
                LlamaServerMustReload: false,
                TurnStillOpen: true,
                CloudAlreadyReady: false,
                LocalStillAlive: false));
        Assert.Equal(RecoveryChatFollowThrough.ContinueThisChat, coldCloud);
    }

    [Fact]
    public void Chooser_list_is_unchanged_when_keys_and_names_match()
    {
        Assert.True(RecoveryReplacementPolicy.SameChooserKeysAndNames(
            ["cloud|a|b", "local|c|d"],
            ["A / b", "c (d)"],
            ["cloud|a|b", "local|c|d"],
            ["A / b", "c (d)"]));
        Assert.False(RecoveryReplacementPolicy.SameChooserKeysAndNames(
            ["cloud|a|b"],
            ["A / b"],
            ["cloud|a|b", "local|c|d"],
            ["A / b", "c (d)"]));
    }

    [Fact]
    public void Fingerprint_mismatch_means_llama_server_must_reload()
    {
        Assert.True(RecoveryReplacementPolicy.LlamaServerMustReload("a|b", "a|c"));
        Assert.False(RecoveryReplacementPolicy.LlamaServerMustReload("a|b", "a|b"));
        Assert.True(RecoveryReplacementPolicy.LlamaServerMustReload("a|b", string.Empty));
    }
}
