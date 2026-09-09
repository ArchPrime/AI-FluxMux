using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class RequestRoutingTopologyTests
{
    [Fact]
    public void Dual_hot_keeps_route_mode_when_both_legs_are_ready()
    {
        var mode = RequestRoutingTopology.ResolveRoutingMode(
            routingEnabled: true,
            localReady: true,
            cloudReady: true,
            topology: RequestRoutingTopology.DualHot);

        Assert.Equal("route", mode);
    }

    [Fact]
    public void Local_prompted_stays_local_when_both_legs_are_ready()
    {
        var mode = RequestRoutingTopology.ResolveRoutingMode(
            routingEnabled: true,
            localReady: true,
            cloudReady: true,
            topology: RequestRoutingTopology.LocalPrompted);

        Assert.Equal("local", mode);
    }

    [Theory]
    [InlineData("LocalPromptedSwitching")]
    [InlineData("local prompted")]
    public void Local_prompted_aliases_normalize(string raw)
    {
        Assert.Equal(RequestRoutingTopology.LocalPrompted, RequestRoutingTopology.Normalize(raw));
    }

    [Theory]
    [InlineData(RequestRoutingTopology.DualHot, "Local and cloud both launched")]
    [InlineData(RequestRoutingTopology.LocalPrompted, "Local by default")]
    public void DescribeForUi_matches_topology(string topology, string expected)
    {
        Assert.Equal(expected, RequestRoutingTopology.DescribeForUi(topology));
    }

    [Theory]
    [InlineData("Local by default", RequestRoutingTopology.LocalPrompted)]
    [InlineData("Local and cloud both launched", RequestRoutingTopology.DualHot)]
    public void NormalizeDisplayOption_maps_ui_labels(string display, string expected)
    {
        Assert.Equal(expected, RequestRoutingTopology.NormalizeDisplayOption(display));
    }
}
