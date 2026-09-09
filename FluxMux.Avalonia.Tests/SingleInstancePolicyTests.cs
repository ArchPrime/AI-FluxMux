using System;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class SingleInstancePolicyTests
{
    [Fact]
    public void New_mutex_takes_ownership()
    {
        var peers = new[] { new SingleInstancePeer(10, HasMainWindow: true, Age: TimeSpan.FromMinutes(5)) };
        Assert.Equal(SingleInstanceDecision.TakeOwnership, SingleInstancePolicy.Decide(true, peers));
    }

    [Fact]
    public void Visible_peer_is_replaced()
    {
        var peers = new[] { new SingleInstancePeer(10, HasMainWindow: true, Age: TimeSpan.FromMinutes(5)) };
        Assert.Equal(SingleInstanceDecision.ReplaceExisting, SingleInstancePolicy.Decide(false, peers));
    }

    [Fact]
    public void Headless_peer_is_replaced()
    {
        var peers = new[] { new SingleInstancePeer(10, HasMainWindow: false, Age: TimeSpan.FromSeconds(1)) };
        Assert.Equal(SingleInstanceDecision.ReplaceExisting, SingleInstancePolicy.Decide(false, peers));
    }

    [Fact]
    public void Abandoned_mutex_without_peers_is_replaced()
    {
        Assert.Equal(
            SingleInstanceDecision.ReplaceExisting,
            SingleInstancePolicy.Decide(false, Array.Empty<SingleInstancePeer>()));
    }
}
