using System;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalValidateLaunchPolicyTests
{
    [Fact]
    public void Decide_reloads_when_llama_server_is_not_running()
        => Assert.Equal(
            LocalValidateLaunchAction.Reload,
            LocalValidateLaunchPolicy.Decide(
                localAlive: false,
                sameLocalModel: false,
                sameLaunchFingerprint: false));

    [Fact]
    public void Decide_probes_a_running_model_after_rename_when_launch_settings_match()
        => Assert.Equal(
            LocalValidateLaunchAction.ProbeRunning,
            LocalValidateLaunchPolicy.Decide(
                localAlive: true,
                sameLocalModel: true,
                sameLaunchFingerprint: true));

    [Fact]
    public void Decide_reloads_when_launch_settings_changed()
        => Assert.Equal(
            LocalValidateLaunchAction.Reload,
            LocalValidateLaunchPolicy.Decide(
                localAlive: true,
                sameLocalModel: true,
                sameLaunchFingerprint: false));

    [Fact]
    public void Decide_reloads_when_a_different_local_model_is_selected()
        => Assert.Equal(
            LocalValidateLaunchAction.Reload,
            LocalValidateLaunchPolicy.Decide(
                localAlive: true,
                sameLocalModel: false,
                sameLaunchFingerprint: false));

    [Fact]
    public void ConfirmStopMessage_warns_that_an_in_flight_turn_will_be_cut_off()
        => Assert.Contains(
            "cut that reply off",
            LocalValidateLaunchPolicy.ConfirmStopMessage(endpointTurnInFlight: true),
            StringComparison.Ordinal);

    [Fact]
    public void ConfirmStopMessage_warns_when_llama_server_is_still_running()
        => Assert.Contains(
            "llama-server is still running",
            LocalValidateLaunchPolicy.ConfirmStopMessage(endpointTurnInFlight: false),
            StringComparison.Ordinal);
}
