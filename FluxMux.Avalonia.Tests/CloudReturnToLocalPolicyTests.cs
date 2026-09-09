using System;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class CloudReturnToLocalPolicyTests
{
    [Fact]
    public void Cline_is_offered_on_the_first_turn_the_local_could_have_taken()
    {
        Assert.Equal(1, CloudReturnToLocalPolicy.RequiredSufficientTurns(harnessEndpoint: false));
        Assert.True(CloudReturnToLocalPolicy.ShouldOffer(
            inApprovedCloudWindow: true,
            localReady: true,
            localWouldHaveSufficed: true,
            returnArmed: true,
            streakAfterThisTurn: 1,
            harnessEndpoint: false,
            alreadyPendingReturn: false));
        Assert.False(CloudReturnToLocalPolicy.WaitForThisTurn(harnessEndpoint: false));
    }

    [Fact]
    public void Harness_hot_hop_is_offered_on_the_first_capable_turn()
    {
        Assert.Equal(1, CloudReturnToLocalPolicy.RequiredSufficientTurns(harnessEndpoint: true));
        Assert.True(CloudReturnToLocalPolicy.ShouldOffer(
            inApprovedCloudWindow: true,
            localReady: true,
            localWouldHaveSufficed: true,
            returnArmed: true,
            streakAfterThisTurn: 1,
            harnessEndpoint: true,
            alreadyPendingReturn: false));
        Assert.False(CloudReturnToLocalPolicy.WaitForThisTurn(harnessEndpoint: true));
    }

    [Fact]
    public void Decline_disarms_until_a_turn_that_needed_cloud()
    {
        Assert.False(CloudReturnToLocalPolicy.ShouldOffer(
            inApprovedCloudWindow: true,
            localReady: true,
            localWouldHaveSufficed: true,
            returnArmed: false,
            streakAfterThisTurn: 3,
            harnessEndpoint: true,
            alreadyPendingReturn: false));
        Assert.True(CloudReturnToLocalPolicy.RearmAfterThisTurn(
            localWouldHaveSufficed: false,
            currentlyArmed: false));
        Assert.False(CloudReturnToLocalPolicy.RearmAfterThisTurn(
            localWouldHaveSufficed: true,
            currentlyArmed: false));
    }

    [Fact]
    public void Return_offer_does_not_hold_the_cloud_turn()
    {
        Assert.False(CloudReturnToLocalPolicy.WaitForThisTurn(harnessEndpoint: true));
        Assert.False(CloudReturnToLocalPolicy.WaitForThisTurn(harnessEndpoint: false));
    }

    [Fact]
    public void Pending_return_offer_still_counts_as_the_cloud_window()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.True(CloudReturnToLocalPolicy.StillHonorCloudWindow(
            "pending",
            CloudReturnToLocalPolicy.Source,
            now + 60,
            now));
        Assert.False(CloudReturnToLocalPolicy.StillHonorCloudWindow(
            "pending",
            RouteRecoveryPolicy.CapacitySource,
            now + 60,
            now));
    }

    [Fact]
    public void Reason_names_the_local_and_whether_this_chat_can_continue()
    {
        var cline = CloudReturnToLocalPolicy.FormatReason("qwen.gguf / coding", harnessEndpoint: false);
        Assert.Contains("qwen.gguf / coding", cline, StringComparison.Ordinal);
        Assert.Contains("This turn continues on the ready cloud", cline, StringComparison.Ordinal);
        Assert.Contains("This Client-app chat can continue", cline, StringComparison.Ordinal);
        Assert.DoesNotContain("new Harness chat", cline, StringComparison.Ordinal);

        var harness = CloudReturnToLocalPolicy.FormatReason("qwen.gguf / coding", harnessEndpoint: true);
        Assert.Contains("This Client-app chat can continue", harness, StringComparison.Ordinal);
        Assert.DoesNotContain("new Harness chat", harness, StringComparison.Ordinal);
    }
}
