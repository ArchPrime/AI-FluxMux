using System;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class DeepSeekHarnessLaunchPolicyTests
{
    [Fact]
    public void Preserve_when_a_Client_app_turn_is_still_on_Port()
    {
        Assert.True(DeepSeekHarnessLaunchPolicy.ShouldPreserveLiveWeb(
            webPortListening: true,
            inFlightChatCount: 1,
            lastClientAppTurnUtc: null,
            utcNow: DateTimeOffset.Parse("2026-09-12T09:06:46Z")));
    }

    [Fact]
    public void Preserve_when_Port_just_finished_a_turn()
    {
        var now = DateTimeOffset.Parse("2026-09-12T09:06:46Z");
        Assert.True(DeepSeekHarnessLaunchPolicy.ShouldPreserveLiveWeb(
            webPortListening: true,
            inFlightChatCount: 0,
            lastClientAppTurnUtc: now.AddSeconds(-30),
            utcNow: now));
    }

    [Fact]
    public void Restart_for_token_is_allowed_when_Port_has_been_quiet()
    {
        var now = DateTimeOffset.Parse("2026-09-12T09:06:46Z");
        Assert.False(DeepSeekHarnessLaunchPolicy.ShouldPreserveLiveWeb(
            webPortListening: true,
            inFlightChatCount: 0,
            lastClientAppTurnUtc: now.AddMinutes(-5),
            utcNow: now));
    }

    [Fact]
    public void Closing_AI_FluxMux_does_not_stop_Harness_web()
    {
        Assert.False(DeepSeekHarnessLaunchPolicy.ShouldStopManagedWebOnFluxMuxExit());
    }

    [Fact]
    public void Hygiene_does_not_kill_Harness_during_a_live_Port_turn()
    {
        var now = DateTimeOffset.Parse("2026-09-12T10:38:23Z");
        Assert.False(DeepSeekHarnessLaunchPolicy.ShouldStopManagedWebForHygiene(
            webPortListening: true,
            inFlightChatCount: 0,
            lastClientAppTurnUtc: now.AddSeconds(-16),
            utcNow: now));
    }

    [Fact]
    public void Hygiene_may_stop_Harness_after_Port_has_been_quiet()
    {
        var now = DateTimeOffset.Parse("2026-09-12T10:38:23Z");
        Assert.True(DeepSeekHarnessLaunchPolicy.ShouldStopManagedWebForHygiene(
            webPortListening: true,
            inFlightChatCount: 0,
            lastClientAppTurnUtc: now.AddMinutes(-5),
            utcNow: now));
    }

    [Fact]
    public void Running_slot_does_not_restart_a_dsh_that_is_already_listening()
    {
        var now = DateTimeOffset.Parse("2026-09-12T10:38:40Z");
        Assert.False(DeepSeekHarnessLaunchPolicy.ShouldRestartWebForFreshAuth(
            webPortListening: true,
            needsFreshAuth: true,
            inFlightChatCount: 0,
            lastClientAppTurnUtc: now.AddSeconds(-10),
            harnessStartedUtc: now.AddSeconds(-20),
            utcNow: now));
    }

    [Fact]
    public void Running_slot_does_not_restart_during_the_start_grace_window()
    {
        var now = DateTimeOffset.Parse("2026-09-12T10:38:40Z");
        Assert.False(DeepSeekHarnessLaunchPolicy.ShouldRestartWebForFreshAuth(
            webPortListening: true,
            needsFreshAuth: true,
            inFlightChatCount: 0,
            lastClientAppTurnUtc: null,
            harnessStartedUtc: now.AddSeconds(-15),
            utcNow: now));
    }

    [Fact]
    public void Idle_Harness_may_restart_for_a_missing_token()
    {
        var now = DateTimeOffset.Parse("2026-09-12T10:38:40Z");
        Assert.True(DeepSeekHarnessLaunchPolicy.ShouldRestartWebForFreshAuth(
            webPortListening: true,
            needsFreshAuth: true,
            inFlightChatCount: 0,
            lastClientAppTurnUtc: now.AddMinutes(-5),
            harnessStartedUtc: now.AddMinutes(-5),
            utcNow: now));
    }

    [Fact]
    public void Do_not_preserve_when_Harness_web_is_not_listening()
    {
        Assert.False(DeepSeekHarnessLaunchPolicy.ShouldPreserveLiveWeb(
            webPortListening: false,
            inFlightChatCount: 2,
            lastClientAppTurnUtc: DateTimeOffset.UtcNow,
            utcNow: DateTimeOffset.UtcNow));
    }

    [Fact]
    public void TryParseServedUtc_reads_last_served_stamp()
    {
        Assert.True(DeepSeekHarnessLaunchPolicy.TryParseServedUtc(
            "2026-09-12T09:06:16Z",
            out var served));
        Assert.Equal(DateTimeOffset.Parse("2026-09-12T09:06:16Z"), served);
        Assert.False(DeepSeekHarnessLaunchPolicy.TryParseServedUtc(" ", out _));
    }
}
