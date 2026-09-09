using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalReasoningLaunchPolicyTests
{
    [Theory]
    [InlineData("On", "On")]
    [InlineData("on", "On")]
    [InlineData("Auto", "Auto")]
    [InlineData("AUTO", "Auto")]
    [InlineData("Off", "Off")]
    [InlineData("", "Off")]
    [InlineData(null, "Off")]
    public void NormalizeMode_maps_known_values(string? input, string expected)
        => Assert.Equal(expected, LocalReasoningLaunchPolicy.NormalizeMode(input));

    [Fact]
    public void IsOff_IsOn_IsAuto_follow_normalized_mode()
    {
        Assert.True(LocalReasoningLaunchPolicy.IsOff("off"));
        Assert.False(LocalReasoningLaunchPolicy.IsOn("off"));
        Assert.False(LocalReasoningLaunchPolicy.IsAuto("off"));

        Assert.True(LocalReasoningLaunchPolicy.IsOn("On"));
        Assert.True(LocalReasoningLaunchPolicy.IsAuto("auto"));
    }
}
